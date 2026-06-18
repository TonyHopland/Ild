using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

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

        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
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

            string? conflictMessage = null;
            foreach (var m in mergeable)
            {
                var rec = records[m.WorkItemId];
                var result = await _repoManager.MergeAsync(
                    worktreePath, m.BranchName!, $"combined preview: merge {m.BranchName}", cancellationToken);
                if (result.Success)
                {
                    rec.MergeStatus = "clean";
                    continue;
                }
                if (result.ConflictedFiles.Count == 0)
                {
                    await _repoManager.AbortMergeAsync(worktreePath, cancellationToken);
                    throw new InvalidOperationException(
                        $"Failed to merge {m.BranchName} into the integration branch: {result.Error}");
                }

                // Conflict is signal, not failure: stop at the first one and report it.
                rec.MergeStatus = "conflict";
                rec.ConflictedFiles = result.ConflictedFiles.ToList();
                if (request.OnConflict == CombinedPreviewConflictMode.ResolveInWorktree)
                {
                    conflictMessage =
                        $"Merge conflict in #{m.WorkItemId} ({m.BranchName}). Markers were left in the integration worktree for manual resolution.";
                }
                else
                {
                    await _repoManager.AbortMergeAsync(worktreePath, cancellationToken);
                    conflictMessage =
                        $"Merge conflict in #{m.WorkItemId} ({m.BranchName}). Skip this branch or resolve it in the worktree.";
                }
                break;
            }

            entry.Members.AddRange(OrderedRecords(records, mergeable, resolved));
            _registry.Set(entry);

            var conflicted = entry.Members.Any(r => r.MergeStatus == "conflict");
            if (conflicted)
            {
                // A conflict halts the composition: no preview is started until it
                // is skipped or resolved. The worktree is retained for inspection.
                return BuildResponse(entry, "conflict", preview: null, message: conflictMessage);
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
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CombinedPreviewResponse> GetAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(workItemIds);
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one work item to preview together.");

        var key = CombinedPreviewNaming.KeyFor(ids);
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
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

            return BuildResponse(entry, state, preview, message: null, stale: anyStale, liveBranches: current);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CombinedPreviewResponse> StopAsync(IReadOnlyList<string> workItemIds, CancellationToken cancellationToken = default)
    {
        var ids = NormalizeIds(workItemIds);
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one work item to preview together.");

        var key = CombinedPreviewNaming.KeyFor(ids);
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
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
        }
        finally
        {
            gate.Release();
        }
    }

    // --- helpers -----------------------------------------------------------

    private sealed record ResolvedMember(string WorkItemId, string? Title, string? BranchName, Guid? RepositoryId);

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
        IReadOnlyDictionary<string, string?>? liveBranches = null)
    {
        return new CombinedPreviewResponse
        {
            IntegrationBranch = entry.IntegrationBranch,
            State = state,
            Stale = stale,
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
