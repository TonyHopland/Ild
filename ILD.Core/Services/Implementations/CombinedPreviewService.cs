using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <inheritdoc cref="ICombinedPreviewService"/>
public sealed class CombinedPreviewService : ICombinedPreviewService
{
    // Per-selection mutex, ref-counted so a gate is dropped once no operation
    // holds it — the dictionary never grows past the set of in-flight keys.
    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private static readonly Dictionary<string, Gate> Gates = new(StringComparer.Ordinal);
    private static readonly object GatesLock = new();

    private readonly IWorkItemManager _workItems;
    private readonly IProviderStore _providerStore;
    private readonly IRepositoryManager _repoManager;
    private readonly IWorktreePreviewService _previewService;
    private readonly CombinedPreviewRegistry _registry;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CombinedPreviewService>? _logger;

    public CombinedPreviewService(
        IWorkItemManager workItems,
        IProviderStore providerStore,
        IRepositoryManager repoManager,
        IWorktreePreviewService previewService,
        CombinedPreviewRegistry registry,
        IConfiguration configuration,
        ILogger<CombinedPreviewService>? logger = null)
    {
        _workItems = workItems;
        _providerStore = providerStore;
        _repoManager = repoManager;
        _previewService = previewService;
        _registry = registry;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CombinedPreviewResponse> StartAsync(CombinedPreviewStartRequest request, CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(request.WorkItemIds);
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one work item to preview together.");

        var key = CombinedPreviewNaming.KeyFor(ids);
        var branch = CombinedPreviewNaming.BranchFor(ids);
        var skip = new HashSet<string>(request.Skip ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        return await WithGateAsync(key, async () =>
        {
            var resolved = await ResolveMembersAsync(ids);
            var repo = await ResolveRepositoryAsync(resolved);
            var basePath = ResolveBaseRepoPath(repo);
            if (!Directory.Exists(Path.Combine(basePath, ".git")))
                throw new InvalidOperationException("Base repository is not available to compose a combined preview from.");
            var gitAuth = await ResolveGitAuthAsync(repo);
            var defaultBranch = repo.DefaultBranch ?? "main";

            // Records for every selected member; merge order is decided below.
            var records = resolved.ToDictionary(
                m => m.WorkItemId,
                m => new CombinedPreviewMemberRecord
                {
                    WorkItemId = m.WorkItemId,
                    Title = m.Title,
                    MergedBranch = m.BranchName,
                    MergeStatus = m.BranchName == null ? "missing" : skip.Contains(m.WorkItemId) ? "skipped" : "pending",
                },
                StringComparer.Ordinal);

            // Compose deterministically: dependencies before dependents, else
            // selection order. Skipped/missing members never enter the merge.
            var mergeable = resolved.Where(m => m.BranchName != null && !skip.Contains(m.WorkItemId)).ToList();
            mergeable = await OrderByDependencyAsync(mergeable);

            await _repoManager.FetchAsync(basePath, cancellationToken, gitAuth);
            var worktreePath = await _repoManager.CreateWorktreeFromAsync(basePath, branch, $"origin/{defaultBranch}");

            var entry = new CombinedPreviewEntry
            {
                Key = key,
                IntegrationBranch = branch,
                WorktreePath = worktreePath,
                BaseRepoPath = basePath,
                RepositoryId = repo.Id,
            };
            // Persist before merging so a conflict (or hard failure) still leaves a
            // tracked entry that resume/stop can act on, never an orphan worktree.
            entry.Members.AddRange(OrderedRecords(records, mergeable, resolved));
            _registry.Set(entry);

            var pending = mergeable.Select(m => records[m.WorkItemId]).ToList();
            var (conflictMessage, awaiting) = await MergePendingAsync(entry, pending, request.OnConflict, cancellationToken);

            if (entry.Members.Any(r => r.MergeStatus == "conflict"))
            {
                // A conflict halts the composition: no preview is started until it
                // is skipped or resolved. The worktree is retained for inspection.
                return BuildResponse(entry, "conflict", preview: null, message: conflictMessage, awaitingResolution: awaiting);
            }

            var preview = await _previewService.StartAsync(
                worktreePath,
                new WorktreePreviewStartOptions(
                    request.ProfileName,
                    request.SkipInstall,
                    request.PublicHost,
                    request.PortOverrides),
                cancellationToken);

            var excluded = entry.Members.Any(r => r.MergeStatus is "skipped" or "missing");
            var message = excluded
                ? "Partial preview — some members were excluded from the merge."
                : null;
            return BuildResponse(entry, excluded ? "partial" : "running", preview, message);
        }, cancellationToken);
    }

    public async Task<CombinedPreviewResponse> ResumeAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(workItemIds);
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one work item to preview together.");

        var key = CombinedPreviewNaming.KeyFor(ids);
        return await WithGateAsync(key, async () =>
        {
            var entry = _registry.Get(key);
            if (entry == null)
                throw new InvalidOperationException("There is no combined preview to resume — start one first.");

            // Still-unresolved markers? Surface them and keep waiting for the human.
            var unmerged = await _repoManager.GetUnmergedFilesAsync(entry.WorktreePath, cancellationToken);
            if (unmerged.Count > 0)
            {
                var stillConflicted = entry.Members.FirstOrDefault(r => r.MergeStatus == "conflict");
                if (stillConflicted != null)
                    stillConflicted.ConflictedFiles = unmerged.ToList();
                return BuildResponse(entry, "conflict", preview: null,
                    message: "The integration worktree still has unresolved conflicts. Resolve the marked files and commit them, then continue.",
                    awaitingResolution: true);
            }

            // The human resolved the conflict; finish the merge commit if they left it staged.
            if (await _repoManager.IsMergeInProgressAsync(entry.WorktreePath, cancellationToken))
            {
                if (!await _repoManager.CommitAsync(entry.WorktreePath, "combined preview: resolved merge conflict"))
                    throw new InvalidOperationException("Failed to commit the resolved merge in the integration worktree.");
            }

            foreach (var rec in entry.Members.Where(r => r.MergeStatus == "conflict"))
            {
                rec.MergeStatus = "clean";
                rec.ConflictedFiles = new List<string>();
            }

            // Continue with members the halted merge never reached.
            var pending = entry.Members.Where(r => r.MergeStatus == "pending").ToList();
            var (conflictMessage, awaiting) = await MergePendingAsync(
                entry, pending, CombinedPreviewConflictMode.ResolveInWorktree, cancellationToken);
            if (entry.Members.Any(r => r.MergeStatus == "conflict"))
                return BuildResponse(entry, "conflict", preview: null, message: conflictMessage, awaitingResolution: awaiting);

            var preview = await _previewService.StartAsync(entry.WorktreePath, new WorktreePreviewStartOptions(), cancellationToken);
            var excluded = entry.Members.Any(r => r.MergeStatus is "skipped" or "missing");
            return BuildResponse(entry, excluded ? "partial" : "running", preview,
                excluded ? "Partial preview — some members were excluded from the merge." : null);
        }, cancellationToken);
    }

    public async Task<CombinedPreviewResponse> GetAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(workItemIds);
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one work item to preview together.");

        var key = CombinedPreviewNaming.KeyFor(ids);
        return await WithGateAsync(key, async () =>
        {
            var entry = _registry.Get(key);
            if (entry == null)
                return await BuildPlanAsync(ids);

            // Staleness: a member that re-ran since composition now resolves to a
            // different current branch. The user rebuilds; nothing auto-refreshes.
            var current = (await ResolveMembersAsync(ids))
                .ToDictionary(m => m.WorkItemId, m => m.BranchName, StringComparer.Ordinal);
            var anyStale = false;
            foreach (var rec in entry.Members)
            {
                if (rec.MergeStatus is "skipped" or "missing")
                    continue;
                var live = current.TryGetValue(rec.WorkItemId, out var b) ? b : null;
                if (live != rec.MergedBranch)
                    anyStale = true;
            }

            WorktreePreviewResponse? preview = null;
            try
            {
                preview = await _previewService.GetStatusAsync(entry.WorktreePath, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // No preview profile / not yet started — leave preview null.
            }

            var conflicted = entry.Members.Any(r => r.MergeStatus == "conflict");
            var excluded = entry.Members.Any(r => r.MergeStatus is "skipped" or "missing");
            var state = conflicted ? "conflict"
                : preview?.State == "running" ? (excluded ? "partial" : "running")
                : "stopped";

            // Markers still in the worktree mean the conflict awaits manual resolution.
            var awaiting = conflicted && await _repoManager.IsMergeInProgressAsync(entry.WorktreePath, cancellationToken);

            return BuildResponse(entry, state, preview, message: null, stale: anyStale, liveBranches: current, awaitingResolution: awaiting);
        }, cancellationToken);
    }

    public async Task<CombinedPreviewResponse> StopAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(workItemIds);
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one work item to preview together.");

        var key = CombinedPreviewNaming.KeyFor(ids);
        return await WithGateAsync(key, async () =>
        {
            var entry = _registry.Get(key);
            if (entry == null)
                return await BuildPlanAsync(ids, state: "stopped");

            try
            {
                await _previewService.StopAsync(entry.WorktreePath, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Nothing running — proceed to tear down the worktree anyway.
            }

            // The integration branch is throwaway; tear it down on stop. Member
            // branches and their PRs are never touched.
            await _repoManager.DestroyWorktreeAsync(entry.WorktreePath);
            await _repoManager.DeleteLocalBranchAsync(entry.BaseRepoPath, entry.IntegrationBranch);
            await _repoManager.PruneWorktreesAsync(entry.BaseRepoPath);
            _registry.Remove(key);

            return BuildResponse(entry, "stopped", preview: null, message: null);
        }, cancellationToken);
    }

    // --- helpers -----------------------------------------------------------

    private sealed record ResolvedMember(string WorkItemId, string? Title, string? BranchName, Guid? RepositoryId);

    /// <summary>
    /// Run <paramref name="work"/> under the per-selection mutex. The gate is
    /// ref-counted: acquired before the wait and released after, so the backing
    /// dictionary holds only the keys with operations in flight.
    /// </summary>
    private static async Task<T> WithGateAsync<T>(string key, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = AcquireGate(key);
        try
        {
            await gate.Semaphore.WaitAsync(ct);
            try
            {
                return await work();
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReleaseGate(key, gate);
        }
    }

    private static Gate AcquireGate(string key)
    {
        lock (GatesLock)
        {
            if (!Gates.TryGetValue(key, out var gate))
            {
                gate = new Gate();
                Gates[key] = gate;
            }
            gate.RefCount++;
            return gate;
        }
    }

    private static void ReleaseGate(string key, Gate gate)
    {
        lock (GatesLock)
        {
            if (--gate.RefCount == 0)
                Gates.Remove(key);
        }
    }

    /// <summary>
    /// Merge each pending member in order, mutating its record to clean/conflict.
    /// Halts at the first conflict; under <see cref="CombinedPreviewConflictMode.ResolveInWorktree"/>
    /// the markers are left in place (returns awaiting=true), otherwise the merge
    /// is aborted. Returns the conflict message (null when all merged cleanly).
    /// </summary>
    private async Task<(string? Message, bool AwaitingResolution)> MergePendingAsync(
        CombinedPreviewEntry entry,
        IReadOnlyList<CombinedPreviewMemberRecord> pending,
        CombinedPreviewConflictMode onConflict,
        CancellationToken ct)
    {
        foreach (var rec in pending)
        {
            if (rec.MergedBranch is null)
                continue;

            var mergeRef = await ResolveMergeRefAsync(entry.BaseRepoPath, rec.MergedBranch);
            var result = await _repoManager.MergeAsync(
                entry.WorktreePath, mergeRef, $"combined preview: merge {rec.MergedBranch}", ct);
            if (result.Success)
            {
                rec.MergeStatus = "clean";
                rec.ConflictedFiles = new List<string>();
                continue;
            }
            if (result.ConflictedFiles.Count == 0)
            {
                await _repoManager.AbortMergeAsync(entry.WorktreePath, ct);
                throw new InvalidOperationException(
                    $"Failed to merge {rec.MergedBranch} into the integration branch: {result.Error}");
            }

            // Conflict is signal, not failure: stop at the first one and report it.
            rec.MergeStatus = "conflict";
            rec.ConflictedFiles = result.ConflictedFiles.ToList();
            if (onConflict == CombinedPreviewConflictMode.ResolveInWorktree)
            {
                return ($"Merge conflict in #{rec.WorkItemId} ({rec.MergedBranch}). Resolve the marked files in the integration worktree and commit them, then continue.", true);
            }
            await _repoManager.AbortMergeAsync(entry.WorktreePath, ct);
            return ($"Merge conflict in #{rec.WorkItemId} ({rec.MergedBranch}). Skip this branch or resolve it in the worktree.", false);
        }
        return (null, false);
    }

    /// <summary>
    /// Prefer the member's local branch; fall back to <c>origin/&lt;branch&gt;</c>
    /// when it survives only on the remote (e.g. its worktree was reaped).
    /// </summary>
    private async Task<string> ResolveMergeRefAsync(string basePath, string branch)
        => await _repoManager.LocalBranchExistsAsync(basePath, branch) ? branch : $"origin/{branch}";

    private static List<string> NormalizeIds(IEnumerable<string> ids)
        => ids.Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private async Task<List<ResolvedMember>> ResolveMembersAsync(IReadOnlyList<string> ids)
    {
        var members = new List<ResolvedMember>(ids.Count);
        foreach (var id in ids)
        {
            // GetWorkItemAsync resolves the CURRENT run, so BranchName tracks the
            // latest run's branch — a re-run picks up the new branch, not a snapshot.
            var wi = await _workItems.GetWorkItemAsync(id);
            if (wi == null)
                throw new InvalidOperationException($"Work item #{id} was not found.");
            members.Add(new ResolvedMember(wi.Id, wi.Title, wi.BranchName, wi.RepositoryId));
        }
        return members;
    }

    private async Task<Repository> ResolveRepositoryAsync(IReadOnlyList<ResolvedMember> members)
    {
        var repoIds = members
            .Where(m => m.RepositoryId.HasValue)
            .Select(m => m.RepositoryId!.Value)
            .Distinct()
            .ToList();
        if (repoIds.Count == 0)
            throw new InvalidOperationException("None of the selected work items has a run to compose — nothing to preview.");
        if (repoIds.Count > 1)
            throw new InvalidOperationException("Combined preview requires all selected work items to share one repository.");

        var repo = await _providerStore.GetRepositoryByIdAsync(repoIds[0]);
        if (repo == null)
            throw new InvalidOperationException("Repository for the selected work items was not found.");
        return repo;
    }

    private async Task<GitAuthOptions?> ResolveGitAuthAsync(Repository repo)
    {
        var remoteProvider = await _providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        return remoteProvider == null
            ? null
            : new GitAuthOptions(repo.CloneUrl, remoteProvider.ApiKey, remoteProvider.Type);
    }

    private string ResolveBaseRepoPath(Repository repo)
    {
        var basePath = repo.WorktreesPath;
        if (!string.IsNullOrWhiteSpace(basePath) && Directory.Exists(Path.Combine(basePath, ".git")))
            return basePath!;

        var dataPath = _configuration["App:DataPath"];
        return Path.GetFullPath(Path.Combine(
            string.IsNullOrWhiteSpace(dataPath) ? "data" : dataPath,
            "repos", repo.Id.ToString("N")));
    }

    /// <summary>
    /// Stable topological order: a member is placed once all of its in-selection
    /// dependencies are placed; ties (and dependency cycles) fall back to
    /// selection order. Deterministic for a given selection.
    /// </summary>
    private async Task<List<ResolvedMember>> OrderByDependencyAsync(List<ResolvedMember> members)
    {
        if (members.Count <= 1)
            return members;

        var inSet = members.Select(m => m.WorkItemId).ToHashSet(StringComparer.Ordinal);
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            var dependencies = await _workItems.GetDependenciesAsync(m.WorkItemId);
            deps[m.WorkItemId] = dependencies
                .Select(d => d.Id)
                .Where(inSet.Contains)
                .ToHashSet(StringComparer.Ordinal);
        }

        var ordered = new List<ResolvedMember>(members.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = new List<ResolvedMember>(members);
        while (remaining.Count > 0)
        {
            var idx = remaining.FindIndex(m => deps[m.WorkItemId].All(placed.Contains));
            if (idx < 0) idx = 0; // cycle — keep selection order
            var next = remaining[idx];
            remaining.RemoveAt(idx);
            ordered.Add(next);
            placed.Add(next.WorkItemId);
        }
        return ordered;
    }

    /// <summary>Merge-ordered members first, then excluded ones in selection order.</summary>
    private static IEnumerable<CombinedPreviewMemberRecord> OrderedRecords(
        IReadOnlyDictionary<string, CombinedPreviewMemberRecord> records,
        IReadOnlyList<ResolvedMember> mergeOrder,
        IReadOnlyList<ResolvedMember> selectionOrder)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in mergeOrder)
        {
            if (seen.Add(m.WorkItemId))
                yield return records[m.WorkItemId];
        }
        foreach (var m in selectionOrder)
        {
            if (seen.Add(m.WorkItemId))
                yield return records[m.WorkItemId];
        }
    }

    private async Task<CombinedPreviewResponse> BuildPlanAsync(IReadOnlyList<string> ids, string state = "notStarted")
    {
        var resolved = await ResolveMembersAsync(ids);
        var response = new CombinedPreviewResponse
        {
            IntegrationBranch = CombinedPreviewNaming.BranchFor(ids),
            State = state,
            Members = resolved.Select(m => new CombinedPreviewMemberResponse
            {
                WorkItemId = m.WorkItemId,
                Title = m.Title,
                BranchName = m.BranchName,
                MergeStatus = m.BranchName == null ? "missing" : "pending",
            }).ToList(),
        };
        return response;
    }

    private static CombinedPreviewResponse BuildResponse(
        CombinedPreviewEntry entry,
        string state,
        WorktreePreviewResponse? preview,
        string? message,
        bool stale = false,
        IReadOnlyDictionary<string, string?>? liveBranches = null,
        bool awaitingResolution = false)
    {
        return new CombinedPreviewResponse
        {
            IntegrationBranch = entry.IntegrationBranch,
            State = state,
            Stale = stale,
            AwaitingResolution = awaitingResolution,
            WorktreePath = entry.WorktreePath,
            Message = message,
            Preview = preview,
            Members = entry.Members.Select(r =>
            {
                var memberStale = liveBranches != null
                    && r.MergeStatus is not ("skipped" or "missing")
                    && (liveBranches.TryGetValue(r.WorkItemId, out var live) ? live : null) != r.MergedBranch;
                return new CombinedPreviewMemberResponse
                {
                    WorkItemId = r.WorkItemId,
                    Title = r.Title,
                    BranchName = r.MergedBranch,
                    MergeStatus = r.MergeStatus,
                    ConflictedFiles = r.ConflictedFiles.ToList(),
                    Stale = memberStale,
                };
            }).ToList(),
        };
    }
}
