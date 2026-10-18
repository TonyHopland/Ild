using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;

namespace ILD.Core.Services.Implementations.Storage;

public sealed class StorageService : IStorageService
{
    private readonly ILoopRunStore _runs;
    private readonly IProviderStore _providers;
    private readonly IWorkItemManager _workItems;
    private readonly IRunReclaimer _reclaimer;
    private readonly IConfiguration _config;
    private readonly string _dataRoot;
    private readonly string _worktreesRoot;
    private readonly string _chatRoot;

    public StorageService(
        ILoopRunStore runs,
        IProviderStore providers,
        IWorkItemManager workItems,
        IRunReclaimer reclaimer,
        IConfiguration config,
        ChatOptions chatOptions)
    {
        _runs = runs;
        _providers = providers;
        _workItems = workItems;
        _reclaimer = reclaimer;
        _config = config;
        _dataRoot = Path.GetFullPath(HostPath(config, "App:DataPath"));
        _worktreesRoot = Path.GetFullPath(HostPath(config, "App:WorktreesPath"));
        _chatRoot = Path.GetFullPath(chatOptions.ScratchRoot);
    }

    public async Task<StorageOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var repositories = (await _providers.GetAllRepositoriesAsync()).ToDictionary(r => r.Id);
        var runs = await _runs.GetRunsWithWorktreeAsync();

        var worktrees = new CategoryTally("worktrees", "Worktrees", hasBreakdown: true);
        var clones = new CategoryTally("repository-clones", "Repository clones", hasBreakdown: true);
        var previewData = new CategoryTally("preview-data", "Preview data", hasBreakdown: true);
        var chats = new CategoryTally("chat-sessions", "Chat sessions", hasBreakdown: false);
        var agents = new CategoryTally("agent-installs", "Agent installs", hasBreakdown: false);
        var temp = new CategoryTally("temp", "Temp", hasBreakdown: true);
        var other = new CategoryTally("other", "Other ILD data", hasBreakdown: false);

        // Most specific first: a directory two measured paths reach is counted by the first.
        var scan = new DiskUsageScan();
        foreach (var run in runs)
            scan.Add(run.WorktreePath!, RepositoryEntry(worktrees, run.RepositoryId, repositories), worktrees.Shared);

        foreach (var repository in repositories.Values)
            if (BaseRepoPath.Existing(repository, _config) is { } clone)
                scan.Add(clone, RepositoryEntry(clones, repository.Id, repositories), clones.Shared);
        var reposStore = BaseRepoPath.StoreRoot(_config);
        var notAClone = clones.Entry("unowned", "Not a repository's clone");
        foreach (var directory in Subdirectories(reposStore, notAClone))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var repositoryId))
                scan.Add(directory, RepositoryEntry(clones, repositoryId, repositories), clones.Shared);
        scan.Add(reposStore, notAClone, clones.Shared);

        var previewDataRoot = PreviewVolumes.Root(_config);
        var notPreviewData = previewData.Entry("unowned", "Not a repository's preview data");
        foreach (var directory in Subdirectories(previewDataRoot, notPreviewData))
            if (Guid.TryParseExact(Path.GetFileName(directory), "D", out var repositoryId))
                scan.Add(directory, PreviewDataEntry(previewData, repositoryId, repositories), previewData.Shared);
        scan.Add(previewDataRoot, notPreviewData, previewData.Shared);

        scan.Add(_worktreesRoot, worktrees.Entry("unowned", "Not owned by any run"), worktrees.Shared);
        scan.Add(_chatRoot, chats.Entry("all", chats.Label), chats.Shared);
        scan.Add(Path.Combine(_dataRoot, ManagedAgentInstall.InstallsDirectory), agents.Entry("all", agents.Label), agents.Shared);

        scan.Add(AgentIsolation.PrivateRoot, temp.Entry("private", "Orchestrator private"), temp.Shared);
        if (AgentReadRoot() is { } agentReadRoot)
            scan.Add(agentReadRoot, temp.Entry("agent-read", "Agent read-only"), temp.Shared);
        else
            temp.NotMeasured("agent-read", "Agent read-only");
        // Unset, the scratch root is the process temp directory, which is not ILD's alone.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar)))
            scan.Add(AgentIsolation.ScratchRoot, temp.Entry("scratch", "Agent scratch"), temp.Shared);
        else
            temp.NotMeasured("scratch", "Agent scratch");

        scan.Add(_dataRoot, other.Entry("all", other.Label), other.Shared);

        var measured = FileStatus.IsSupported;
        if (measured)
            scan.Run(cancellationToken);
        return new StorageOverview(
            new[] { worktrees, clones, previewData, chats, agents, temp, other }.Select(c => c.ToCategory(measured)).ToList());
    }

    public async Task<WorktreeCleanupCandidates> GetCleanupCandidatesAsync(CancellationToken cancellationToken)
    {
        var activeWorkItemIds = await _runs.GetActiveWorkItemIdsAsync();
        var runs = (await _runs.GetRunsWithWorktreeAsync())
            .Where(r => RunIneligibility(r, activeWorkItemIds) is null)
            .ToList();

        var workItems = new Dictionary<string, WorkItemView?>(StringComparer.Ordinal);
        foreach (var workItemId in runs.Select(r => r.WorkItemId).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            workItems[workItemId] = await _workItems.GetWorkItemAsync(workItemId);
        }
        var eligible = runs.Where(r => WorkItemIneligibility(workItems[r.WorkItemId]) is null).ToList();

        var repositories = (await _providers.GetAllRepositoriesAsync()).ToDictionary(r => r.Id);
        var candidates = eligible.Select(run =>
        {
            var (bytes, incomplete) = Freed([run.WorktreePath!], cancellationToken);
            var repositoryName = run.RepositoryId is { } id ? repositories.GetValueOrDefault(id)?.Name : null;
            return new WorktreeCleanupCandidate(
                run.Id, run.WorkItemId, workItems[run.WorkItemId]!.Title, run.RepositoryId, repositoryName, bytes, incomplete);
        }).ToList();
        var (totalBytes, _) = Freed(eligible.Select(r => r.WorktreePath!), cancellationToken);
        return new WorktreeCleanupCandidates(candidates, totalBytes);
    }

    public async Task<WorktreeCleanupResult> CleanUpWorktreesAsync(IReadOnlyCollection<Guid> runIds)
    {
        var results = new List<WorktreeCleanupRunResult>();
        var actedOnAny = false;
        foreach (var runId in runIds.Distinct())
        {
            var run = await _runs.GetByIdAsync(runId);
            if (run is null)
            {
                results.Add(new(runId, WorktreeCleanupOutcome.Skipped, null, RunNotFound));
                continue;
            }

            WorkItemView? workItem;
            try
            {
                workItem = await _workItems.GetWorkItemAsync(run.WorkItemId);
            }
            catch (Exception ex) when (actedOnAny && WorkItemServerUnreachable.Is(ex))
            {
                results.Add(new(runId, WorktreeCleanupOutcome.Skipped, null, "The WorkItem Server could not be reached."));
                continue;
            }

            // Reading the work item tracks all its runs in this scope, so a run read
            // before it, in this pass or an earlier one, holds values from back then.
            await _runs.ReloadAsync(run);
            var reason = RunIneligibility(run, await _runs.GetActiveWorkItemIdsAsync()) ?? WorkItemIneligibility(workItem);
            if (reason is not null)
            {
                results.Add(new(runId, WorktreeCleanupOutcome.Skipped, null, reason));
                continue;
            }

            var (bytes, _) = Freed([run.WorktreePath!], CancellationToken.None);
            actedOnAny = true;
            results.Add(await RunCleanup.CleanUpAsync(run, _reclaimer, _runs) switch
            {
                RunCleanupOutcome.Cleaned => new(runId, WorktreeCleanupOutcome.Cleaned, bytes, null),
                RunCleanupOutcome.ReclaimFailed => new(runId, WorktreeCleanupOutcome.Failed, null, "Could not verify the worktree and branch were removed; the run was left as it was."),
                RunCleanupOutcome.RunGone => new(runId, WorktreeCleanupOutcome.Skipped, null, RunNotFound),
                _ => new(runId, WorktreeCleanupOutcome.Skipped, null, NotFinished),
            });
        }

        var totalBytes = FileStatus.IsSupported
            ? results.Where(r => r.Outcome == WorktreeCleanupOutcome.Cleaned).Sum(r => r.Bytes ?? 0)
            : (long?)null;
        return new WorktreeCleanupResult(results, totalBytes);
    }

    private const string RunNotFound = "The run does not exist.";
    private const string NotFinished = "The run has not finished.";

    private static string? RunIneligibility(LoopRun run, IReadOnlyCollection<string> activeWorkItemIds)
    {
        if (string.IsNullOrEmpty(run.WorktreePath))
            return "The run has no worktree.";
        if (!RunCleanup.IsFinished(run))
            return NotFinished;
        if (run.Retain)
            return "The run is pinned.";
        if (activeWorkItemIds.Contains(run.WorkItemId, StringComparer.Ordinal))
            return "Its work item has an active run.";
        return null;
    }

    private static string? WorkItemIneligibility(WorkItemView? workItem)
    {
        if (workItem is null)
            return "Its work item was not found on the WorkItem Server.";
        if (workItem.Status != RemoteWorkItemStatus.Done)
            return "Its work item is no longer Done.";
        return null;
    }

    /// <summary>What removing <paramref name="worktrees"/> together frees: files no link outside them keeps.</summary>
    private static (long? Bytes, bool Incomplete) Freed(IEnumerable<string> worktrees, CancellationToken cancellationToken)
    {
        if (!FileStatus.IsSupported)
            return (null, false);
        var freed = new UsageTally();
        var scan = new DiskUsageScan();
        foreach (var worktree in worktrees)
            scan.Add(worktree, freed, shared: new UsageTally());
        scan.Run(cancellationToken);
        return (freed.Bytes, freed.Incomplete);
    }

    private static UsageTally RepositoryEntry(CategoryTally category, Guid? repositoryId, IReadOnlyDictionary<Guid, Repository> repositories)
    {
        if (repositoryId is not { } id)
            return category.Entry("no-repository", "No repository");
        var name = repositories.GetValueOrDefault(id)?.Name;
        return category.Entry(id.ToString(), name ?? "Repository no longer exists", id, name);
    }

    /// <summary>
    /// A repository's persisted preview data, marked when nothing uses it any more —
    /// the repository is gone or keeps no preview data — since only this page removes it then.
    /// </summary>
    private static UsageTally PreviewDataEntry(CategoryTally category, Guid repositoryId, IReadOnlyDictionary<Guid, Repository> repositories)
    {
        var repository = repositories.GetValueOrDefault(repositoryId);
        var label = repository switch
        {
            null => "Repository no longer exists",
            { KeepPreviewData: false } => $"{repository.Name} (Keep preview data is off)",
            _ => repository.Name,
        };
        return category.Entry(repositoryId.ToString(), label, repositoryId, repository?.Name);
    }

    /// <summary>A path the host sets at startup (Program); there is no default to fall back on here.</summary>
    private static string HostPath(IConfiguration config, string key)
        => config[key] is { } path && !string.IsNullOrWhiteSpace(path) ? path : throw new InvalidOperationException($"{key} is not set.");

    private static string? AgentReadRoot()
    {
        try
        {
            return AgentIsolation.AgentReadRoot;
        }
        catch (InvalidOperationException)
        {
            // Isolation on without the root configured: there is no root to measure.
            return null;
        }
    }

    private static IReadOnlyList<string> Subdirectories(string path, UsageTally onUnreadable)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            onUnreadable.MarkIncomplete();
            return [];
        }
    }

    private sealed class CategoryTally(string key, string label, bool hasBreakdown)
    {
        // A null tally is an entry that is not measured.
        private readonly List<(StorageEntry Head, UsageTally? Tally)> _entries = [];

        public string Label => label;
        public UsageTally Shared { get; } = new();

        public UsageTally Entry(string entryKey, string entryLabel, Guid? repositoryId = null, string? repositoryName = null)
        {
            if (_entries.FirstOrDefault(e => e.Head.Key == entryKey).Tally is { } existing)
                return existing;
            var tally = new UsageTally();
            _entries.Add((new StorageEntry(entryKey, entryLabel, repositoryId, repositoryName, null, false), tally));
            return tally;
        }

        public void NotMeasured(string entryKey, string entryLabel)
            => _entries.Add((new StorageEntry(entryKey, entryLabel, null, null, null, false), null));

        public StorageCategory ToCategory(bool measured)
        {
            var entries = _entries
                .Select(e => measured && e.Tally is { } tally ? e.Head with { Bytes = tally.Bytes, Incomplete = tally.Incomplete } : e.Head)
                .ToList();
            if (measured && Shared.Bytes > 0)
                entries.Add(new StorageEntry("shared", "Shared with other entries (hard links)", null, null, Shared.Bytes, Shared.Incomplete));

            // The sum of what was measured; an entry that was not makes it incomplete, not smaller.
            var bytes = measured ? entries.Sum(e => e.Bytes ?? 0) : (long?)null;
            var incomplete = entries.Any(e => e.Incomplete) || (measured && entries.Any(e => e.Bytes is null));
            return new StorageCategory(key, label, bytes, incomplete, hasBreakdown ? entries : []);
        }
    }
}
