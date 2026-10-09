using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Remote-backed implementation of <see cref="IWorkItemManager"/>.
/// The WorkItemServer is authoritative for the work-item domain.
/// Engine-only fields (worktree, branch, PR, current loop run) live on LoopRun.
/// </summary>
public class WorkItemManager : IWorkItemManager
{
    private readonly IRepositoryManager _repoManager;
    private readonly IProviderStore _providerStore;
    private readonly IEventLogService _eventLog;
    private readonly ILoopRunStore _loopRunStore;
    private readonly IWorkItemNotifier _notifier;
    private readonly IWorkItemServerClient _server;
    private readonly IWorkItemServerOptionsResolver _options;
    private readonly IWorktreePreviewService _previewService;
    private readonly IWorkItemScheduler? _scheduler;
    private readonly ILoopEngine? _engine;
    private readonly IRunReclaimer _runReclaimer;
    private readonly IRemoteProvider? _remoteProvider;
    private readonly ILogger<WorkItemManager> _logger;

    public WorkItemManager(
        IRepositoryManager repoManager,
        IProviderStore providerStore,
        IEventLogService eventLog,
        ILoopRunStore loopRunStore,
        IWorkItemServerClient server,
        IWorkItemServerOptionsResolver options,
        IWorkItemNotifier? notifier = null,
        IWorktreePreviewService? previewService = null,
        IWorkItemScheduler? scheduler = null,
        ILoopEngine? engine = null,
        IRunReclaimer? runReclaimer = null,
        IRemoteProvider? remoteProvider = null,
        ILogger<WorkItemManager>? logger = null)
    {
        _repoManager = repoManager;
        _providerStore = providerStore;
        _eventLog = eventLog;
        _loopRunStore = loopRunStore;
        _server = server;
        _options = options;
        _notifier = notifier ?? new NoopWorkItemNotifier();
        _previewService = previewService ?? new NoopPreviewService();
        _scheduler = scheduler;
        _engine = engine;
        _runReclaimer = runReclaimer ?? new RunReclaimer(repoManager, providerStore, _previewService, _notifier);
        _remoteProvider = remoteProvider;
        _logger = logger ?? NullLogger<WorkItemManager>.Instance;
    }

    /// <summary>
    /// Finish a run a human has decided is over: stop it, and leave the row
    /// terminal with a completion timestamp. That terminal status is what takes
    /// the work item out of the Active Work Item Set, so the scheduler stops
    /// heartbeating it and gives its concurrency slot back — a run left alive
    /// behind a finished item holds that slot for the lifetime of the process.
    /// Does <b>not</b> touch the worktree or branch: local git state lives
    /// exactly as long as the run row.
    /// </summary>
    private async Task EndRunAsync(LoopRun run, string reason)
    {
        await StopRunIfActiveAsync(run, reason);

        // Belt and braces, for the no-engine wiring and for a best-effort stop
        // that failed: the row has to end terminal and timestamped, or the
        // retention sweeper never sees it and its work item keeps a slot.
        var endedHere = IsAlive(run.Status);
        if (endedHere)
            run.Status = LoopRunStatus.Cancelled;
        run.CompletedAt ??= DateTime.UtcNow;
        await _loopRunStore.UpdateRunAsync(run);
        if (endedHere)
            await TryRecordAsync(run.Id, EventType.LoopRunCancelled, reason);
    }

    /// <summary>
    /// Record an event about a lifecycle step this manager is taking anyway.
    /// Best-effort, as event writes are in the engine: a write that fails must
    /// not stop the step it describes. For a run-ending event, a
    /// <see cref="RunClosedException"/> means the end is already on record.
    /// </summary>
    private async Task TryRecordAsync(Guid runId, EventType type, string text)
    {
        try { await _eventLog.AppendAsync(runId, type, text); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record {EventType} on run {RunId}", type, runId);
        }
    }

    private static bool IsAlive(LoopRunStatus status)
        => status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman;

    /// <summary>
    /// Stop a still-active run, leaving its work item's status to the caller.
    /// Does <b>not</b> touch the run's worktree or branch — local git state
    /// lives exactly as long as the run row and is reclaimed only when the run
    /// itself is deleted (manual delete or the retention sweeper).
    ///
    /// <see cref="ILoopEngine.StopRunAsync"/> and not <c>CancelRunAsync</c>:
    /// the latter parks the work item in HumanFeedback, and every caller here
    /// is on its way to giving the item a status of its own. Inheriting that
    /// park would pop a needs-attention toast on the card the human just
    /// finished, outliving the status written over it a moment later.
    /// </summary>
    private async Task StopRunIfActiveAsync(LoopRun run, string reason)
    {
        if (run.Status is not (LoopRunStatus.Running or LoopRunStatus.WaitingHuman) || _engine is null)
            return;

        try { await _engine.StopRunAsync(run.Id, reason); } catch { /* best effort */ }
        // StopRunAsync persisted through its own scope; refresh our tracked
        // instance so we don't write stale state back over it.
        try { await _loopRunStore.ReloadAsync(run); } catch { /* row may be gone */ }
    }

    /// <summary>
    /// Best-effort stop of any worktree preview running for the given path so a
    /// finished work item stops hogging its preview ports. No-ops when the path
    /// is empty or no preview is running. Failures are swallowed — preview
    /// teardown must never block a Done transition.
    /// </summary>
    private async Task StopPreviewIfRunningAsync(string workItemId, string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !_previewService.IsPreviewRunning(worktreePath))
            return;

        try
        {
            await _previewService.StopAsync(worktreePath);
            await _notifier.PreviewStateChangedAsync(workItemId);
        }
        catch { /* best effort — never block the Done transition */ }
    }

    // ──────────────────────────────────────────────────────────────────
    // Create / Read / Update
    // ──────────────────────────────────────────────────────────────────

    public Task<string> CreateWorkItemAsync(string title, string description, Guid? repositoryId)
        => CreateWorkItemAsync(title, description, repositoryId, null, false);

    public async Task<string> CreateWorkItemAsync(
        string title,
        string description,
        Guid? repositoryId,
        Guid? createdByLoopRunId,
        bool forceBacklog,
        IEnumerable<string>? tags = null,
        Guid? createdByChatSessionId = null,
        string? branchNameOverride = null,
        string? baseBranchOverride = null)
    {
        var opts = await _options.ResolveForRepositoryAsync(repositoryId);

        RemoteWorkItemStatus? forceStatus = forceBacklog ? RemoteWorkItemStatus.Backlog : null;
        if (!forceBacklog && repositoryId.HasValue)
        {
            var repo = await _providerStore.GetRepositoryByIdAsync(repositoryId.Value)
                ?? throw new InvalidOperationException("Repository not found");
            forceStatus = MapToRemote(repo.DefaultIntakeStatus);
        }

        var createdBy = createdByLoopRunId.HasValue ? $"Agent-{createdByLoopRunId.Value}"
            : createdByChatSessionId.HasValue ? $"Chat-{createdByChatSessionId.Value}"
            : null;

        var serverWi = await _server.CreateAsync(opts, new RemoteCreateWorkItemRequest
        {
            Title = title,
            Description = description,
            CreatedBy = createdBy,
            ForceStatus = forceStatus,
            Tags = tags?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            CreatedByLoopRunId = createdByLoopRunId,
            CreatedByChatSessionId = createdByChatSessionId,
            RepositoryId = repositoryId,
            BranchNameOverride = branchNameOverride,
            BaseBranchOverride = baseBranchOverride,
        });

        // Broadcast the creation so connected clients (e.g. the Taskboard) add
        // the new item live instead of only on a manual refresh. Creation has
        // no prior status, so old and new are the landing status. Covers every
        // creation path — UI and agent/MCP alike — since both flow through here.
        await _notifier.WorkItemStateChangedAsync(serverWi.Id, serverWi.Status, serverWi.Status);

        return serverWi.Id;
    }

    public async Task<WorkItemView?> GetWorkItemAsync(string workItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var remote = await _server.GetAsync(opts, workItemId);
        return remote == null ? null : await ViewOfAsync(opts, remote);
    }

    private async Task<WorkItemView> ViewOfAsync(WorkItemServerOptions opts, RemoteWorkItem remote)
        => await ViewOfAsync(opts, remote, await _loopRunStore.GetAllByWorkItemAsync(remote.Id),
            await _loopRunStore.GetWorkItemStatusReasonAsync(remote.Id));

    /// <summary>A work item the server has just returned, joined with its runs and status reason.</summary>
    private async Task<WorkItemView> ViewOfAsync(
        WorkItemServerOptions opts, RemoteWorkItem remote, IReadOnlyList<LoopRun> runs, WorkItemStatusReason? statusReason)
    {
        await RecordUnreportedPullRequestsAsync(opts, remote, runs);
        var currentRun = CurrentRun(runs);
        return BuildView(remote, currentRun, runs, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty), statusReason);
    }

    public async Task<IReadOnlyList<WorkItemView>> ListAsync(
        RemoteWorkItemStatus? status,
        Guid? createdByLoopRunId,
        Guid? repositoryId,
        int skip,
        int take)
    {
        var page = await ListPageAsync(new WorkItemListQuery
        {
            Status = status,
            CreatedByLoopRunId = createdByLoopRunId,
            RepositoryId = repositoryId,
            OrderBy = WorkItemOrderBy.CreatedAt,
            Skip = skip,
            Take = take,
        });
        return page.Items;
    }

    public async Task<WorkItemPage> ListPageAsync(WorkItemListQuery query)
    {
        var opts = await _options.ResolveForRepositoryAsync(query.RepositoryId);
        // Actionability reads the status of every dependency, so only then is
        // the whole list needed; otherwise the server narrows to the status.
        var all = await _server.ListAsync(opts, query.ActionableOnly ? null : query.Status, tags: null);
        if (all.Count == 0) return new WorkItemPage(Array.Empty<WorkItemView>(), 0);

        // A view's creator is its current run's, falling back to the item's own,
        // so filtering by creator needs every candidate's runs up front.
        IReadOnlyDictionary<string, List<LoopRun>>? candidateRuns = null;
        Func<RemoteWorkItem, Guid?>? creatorOf = null;
        if (query.CreatedByLoopRunId.HasValue)
        {
            candidateRuns = await RunsByWorkItemAsync(all.Select(w => w.Id).ToList());
            creatorOf = w => CurrentRun(candidateRuns.GetValueOrDefault(w.Id) ?? [])?.CreatedByLoopRunId ?? w.CreatedByLoopRunId;
        }

        var matching = ApplyListQuery(all, query, all.ToDictionary(w => w.Id, w => w.Status), creatorOf).ToList();
        var page = matching.Skip(ClampSkip(query.Skip)).Take(ClampTake(query.Take)).ToList();
        if (page.Count == 0) return new WorkItemPage(Array.Empty<WorkItemView>(), matching.Count);

        var pageIds = page.Select(w => w.Id).ToList();
        var runsByWorkItem = candidateRuns ?? await RunsByWorkItemAsync(pageIds);
        var statusReasons = await _loopRunStore.GetWorkItemStatusReasonsAsync(pageIds);
        var views = new List<WorkItemView>(page.Count);
        foreach (var remote in page)
            views.Add(await ViewOfAsync(opts, remote, runsByWorkItem.GetValueOrDefault(remote.Id) ?? [],
                statusReasons.GetValueOrDefault(remote.Id)));
        return new WorkItemPage(views, matching.Count);
    }

    private async Task<IReadOnlyDictionary<string, List<LoopRun>>> RunsByWorkItemAsync(IReadOnlyCollection<string> workItemIds)
        => (await _loopRunStore.GetAllByWorkItemsAsync(workItemIds))
            .GroupBy(r => r.WorkItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>
    /// The run a work item's view reflects: a live one first, then one that
    /// stopped short, then the latest that has not completed.
    /// </summary>
    private static LoopRun? CurrentRun(IReadOnlyList<LoopRun> runs)
        => runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
           ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.WaitingHuman)
           ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.Failed)
           ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.Cancelled)
           ?? runs.Where(r => r.Status != LoopRunStatus.Completed)
                  .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
                  .FirstOrDefault();

    public async Task<IReadOnlyDictionary<RemoteWorkItemStatus, int>> CountByStatusAsync(WorkItemListQuery query)
    {
        var opts = await _options.ResolveForRepositoryAsync(query.RepositoryId);
        var all = await _server.ListAsync(opts, status: null, tags: null);
        var byStatus = ApplyListQuery(all, query with { Status = null }, all.ToDictionary(w => w.Id, w => w.Status))
            .GroupBy(w => w.Status)
            .ToDictionary(g => g.Key, g => g.Count());
        return Enum.GetValues<RemoteWorkItemStatus>().ToDictionary(s => s, s => byStatus.GetValueOrDefault(s));
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync()
    {
        var opts = await _options.ResolveForRepositoryAsync(null);
        var all = await _server.ListAsync(opts, status: null, tags: null);
        // Tags match case-insensitively, so spellings that differ only in case
        // are one tag, listed once under its first spelling in this order.
        return all
            .SelectMany(w => w.Tags)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t, StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<WorkItemSummary>> ListSummariesAsync(WorkItemListQuery query)
    {
        var opts = await _options.ResolveForRepositoryAsync(query.RepositoryId);
        // Load the whole graph (not just the requested page/status) so reverse
        // edges and dependency status resolve across every item — the same
        // full-load pattern GetDependentsAsync uses. No LoopRun merge is needed:
        // every projected field already lives on the server entity.
        var all = await _server.ListAsync(opts, status: null, tags: null);
        if (all.Count == 0) return Array.Empty<WorkItemSummary>();

        var statusById = all.ToDictionary(w => w.Id, w => w.Status);
        var blocksCount = BuildReverseEdgeCounts(all);

        return ApplyListQuery(all, query, statusById)
            .Skip(ClampSkip(query.Skip))
            .Take(ClampTake(query.Take))
            .Select(w => new WorkItemSummary(
                w.Id,
                w.Title,
                w.Description,
                w.Status,
                w.Priority,
                w.Tags,
                w.Dependencies,
                blocksCount.GetValueOrDefault(w.Id),
                IsActionable(w, statusById),
                w.CreatedAt,
                w.UpdatedAt,
                w.RepositoryId,
                w.CreatedByLoopRunId,
                w.CreatedByChatSessionId))
            .ToList();
    }

    private static int ClampSkip(int skip) => skip < 0 ? 0 : skip;
    private static int ClampTake(int take) => take <= 0 ? 100 : take;

    /// <summary>
    /// The filtering and ordering every listing shares, skip and take aside.
    /// <paramref name="creatorOf"/> decides whose creator a run filter matches;
    /// by default the item's own.
    /// </summary>
    private static IOrderedEnumerable<RemoteWorkItem> ApplyListQuery(
        IEnumerable<RemoteWorkItem> items,
        WorkItemListQuery query,
        IReadOnlyDictionary<string, RemoteWorkItemStatus> statusById,
        Func<RemoteWorkItem, Guid?>? creatorOf = null)
    {
        creatorOf ??= w => w.CreatedByLoopRunId;
        var filtered = items;
        if (query.Status.HasValue) filtered = filtered.Where(w => w.Status == query.Status.Value);
        if (query.Priority.HasValue) filtered = filtered.Where(w => w.Priority == query.Priority.Value);
        if (query.RepositoryId.HasValue) filtered = filtered.Where(w => w.RepositoryId == query.RepositoryId.Value);
        if (query.CreatedByLoopRunId.HasValue) filtered = filtered.Where(w => creatorOf(w) == query.CreatedByLoopRunId.Value);
        var wanted = (query.Tags ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count > 0)
        {
            filtered = query.TagMatch == WorkItemTagMatch.All
                ? filtered.Where(w => wanted.IsSubsetOf(w.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                : filtered.Where(w => w.Tags.Any(wanted.Contains));
        }
        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(w =>
                w.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (w.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || w.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
        }
        if (query.ActionableOnly)
            filtered = filtered.Where(w => IsActionable(w, statusById));

        var ordered = query.OrderBy switch
        {
            WorkItemOrderBy.Priority => filtered.OrderByDescending(w => w.Priority).ThenByDescending(w => w.UpdatedAt),
            WorkItemOrderBy.CreatedAt => filtered.OrderByDescending(w => w.CreatedAt),
            _ => filtered.OrderByDescending(w => w.UpdatedAt),
        };
        return ordered.ThenByDescending(w => w.Id, StringComparer.Ordinal);
    }

    public async Task<BacklogSummary> GetBacklogSummaryAsync(Guid? repositoryId)
    {
        var opts = await _options.ResolveForRepositoryAsync(repositoryId);
        var all = await _server.ListAsync(opts, status: null, tags: null);
        // Dependency status is resolved against the whole graph; the counts are
        // scoped to the requested repository (when one is supplied).
        var statusById = all.ToDictionary(w => w.Id, w => w.Status);
        var scoped = repositoryId.HasValue
            ? all.Where(w => w.RepositoryId == repositoryId.Value).ToList()
            : all.ToList();

        var byStatus = scoped
            .GroupBy(w => w.Status)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var byPriority = scoped
            .GroupBy(w => w.Priority)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var actionable = scoped.Count(w => IsActionable(w, statusById));

        return new BacklogSummary(
            scoped.Count,
            byStatus,
            byPriority,
            scoped.Count - actionable,
            actionable);
    }

    /// <summary>
    /// Count, per work item id, how many other items declare it as a
    /// dependency — the reverse "blocks" edges (what frees up if this item
    /// completes).
    /// </summary>
    private static Dictionary<string, int> BuildReverseEdgeCounts(IReadOnlyList<RemoteWorkItem> all)
    {
        var counts = new Dictionary<string, int>();
        foreach (var w in all)
            foreach (var dep in w.Dependencies)
                counts[dep] = counts.GetValueOrDefault(dep) + 1;
        return counts;
    }

    /// <summary>
    /// An item is actionable now when every dependency it lists is Done
    /// (vacuously true when it has none) — the same readiness rule the server
    /// applies in PromoteWorkQueueItemsAsync. A dependency id missing from the
    /// graph counts as not-Done, so the item stays blocked.
    /// </summary>
    private static bool IsActionable(RemoteWorkItem w, IReadOnlyDictionary<string, RemoteWorkItemStatus> statusById)
        => w.Dependencies.All(id => statusById.TryGetValue(id, out var s) && s == RemoteWorkItemStatus.Done);

    /// <summary>
    /// The run whose lifetime defines the work item's Started/Completed times:
    /// the most recently started run regardless of status. A successfully
    /// finished run is <see cref="LoopRunStatus.Completed"/> and is therefore
    /// excluded from the current-run selection above, so it must be picked up
    /// here for the work item to surface its completion time.
    /// </summary>
    private static LoopRun? LatestRun(IEnumerable<LoopRun> runs)
        => runs.OrderByDescending(r => r.StartedAt ?? r.CreatedAt).FirstOrDefault();

    private static WorkItemView BuildView(
        RemoteWorkItem remote,
        LoopRun? run,
        IReadOnlyList<LoopRun> runs,
        bool isPreviewRunning,
        WorkItemStatusReason? statusReason)
    {
        var timingRun = LatestRun(runs);
        return new WorkItemView
        {
            Id = remote.Id,
            Title = remote.Title,
            Description = remote.Description,
            CreatedBy = remote.CreatedBy,
            CreatedAt = remote.CreatedAt,
            UpdatedAt = remote.UpdatedAt,
            Priority = remote.Priority,
            Status = remote.Status,
            Tags = remote.Tags,
            HumanFeedbackActions = remote.HumanFeedbackActions,
            AiProviderOverride = remote.AiProviderOverride,
            AiProviderOverrideId = remote.AiProviderOverrideId,
            BranchNameOverride = remote.BranchNameOverride,
            BaseBranchOverride = remote.BaseBranchOverride,
            RepositoryId = remote.RepositoryId,
            RunRepositoryId = run?.RepositoryId,
            CreatedByLoopRunId = run?.CreatedByLoopRunId ?? remote.CreatedByLoopRunId,
            CreatedByChatSessionId = remote.CreatedByChatSessionId,
            StartedAt = timingRun?.StartedAt,
            CompletedAt = timingRun?.CompletedAt,
            WorktreePath = run?.WorktreePath,
            BranchName = run?.BranchName,
            PrUrl = run?.PrUrl,
            IsPrMerged = run?.IsPrMerged == true,
            HumanFeedbackReason = run?.HumanFeedbackReason,
            StatusReason = statusReason?.Text,
            StatusReasonAt = statusReason?.At,
            CurrentLoopRunId = run?.Id,
            CurrentNodeLabel = ResolveCurrentNodeLabel(run),
            IsPreviewRunning = isPreviewRunning,
            PrStatus = ResolvePrStatus(run?.PrSnapshot),
            PullRequests = BuildPrHistory(remote, runs),
            Attachments = remote.Attachments,
            PendingEditProposalCount = remote.PendingEditProposalCount,
        };
    }

    /// <summary>
    /// One observation of a PR on a work item: what the server holds for the
    /// item, or what a live run still carries. Both collapse into this shape so
    /// the dedup and ordering rules never have to ask where an entry came from.
    /// </summary>
    private readonly record struct PrSighting(
        string Url,
        Guid? RunId,
        bool Merged,
        string? Snapshot,
        DateTime CreatedAt);

    /// <summary>
    /// Every PR ever opened against the work item, deduplicated by URL and
    /// ordered newest run first — the order the detail dialog renders, so the PR
    /// a human most likely wants is at the top.
    ///
    /// The PRs the <b>server</b> holds are the durable record: they survive the
    /// run being reclaimed and this ILD instance being reset. The PRs on live
    /// runs are unioned in so a PR shows the moment a run has it, even before
    /// the server has been told (see
    /// <see cref="RecordUnreportedPullRequestsAsync"/>) — and because only a
    /// live run carries the throwaway state a server record has no business
    /// holding: the heartbeat's badge snapshot, and a run id that still
    /// resolves to something. ADR-0008 gives each run at most one PR, so a URL
    /// seen on several runs (a retry pointed back at its predecessor's PR)
    /// collapses to one entry attributed to the newest of them.
    /// </summary>
    private static IReadOnlyList<WorkItemPullRequest> BuildPrHistory(RemoteWorkItem remote, IReadOnlyList<LoopRun> runs)
    {
        var sightings = runs
            .Where(r => !string.IsNullOrWhiteSpace(r.PrUrl))
            .Select(r => new PrSighting(r.PrUrl!, r.Id, r.IsPrMerged, r.PrSnapshot, RunPrCreatedAt(r)))
            .Concat(remote.PullRequests.Select(p => new PrSighting(p.Url, p.LoopRunId, p.Merged, null, p.CreatedAt)))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        if (sightings.Count == 0) return Array.Empty<WorkItemPullRequest>();

        var liveRunIds = runs.Select(r => r.Id).ToHashSet();

        // GroupBy keeps both the groups and their contents in source order, so
        // the newest sighting leads each group and the groups themselves come
        // out newest-first.
        return sightings
            .GroupBy(s => s.Url, StringComparer.Ordinal)
            .Select(g => new WorkItemPullRequest(
                g.Key,
                // Provenance is only worth reporting while the run is still
                // there to link to; a reclaimed run leaves the entry standing
                // on its own.
                g.Select(s => s.RunId).FirstOrDefault(id => id is { } runId && liveRunIds.Contains(runId)),
                // Merge is monotonic: a later sighting never un-merges a PR.
                g.Any(s => s.Merged),
                ResolvePrStatus(g.FirstOrDefault(s => !string.IsNullOrEmpty(s.Snapshot)).Snapshot),
                g.Min(s => s.CreatedAt)))
            .ToList();
    }

    /// <summary>
    /// When a run's PR entered the work item's history. The run's start, not the
    /// PR's own creation time: it is what orders history, it is stable across
    /// re-reads, and it is all the run row knows.
    /// </summary>
    private static DateTime RunPrCreatedAt(LoopRun run) => run.StartedAt ?? run.CreatedAt;

    /// <summary>
    /// Tell the server about any PR a run carries that the work item does not
    /// already record — the backstop behind the write at PR creation
    /// (<see cref="RecordPullRequestAsync"/>). It carries PRs opened before this
    /// instance knew to report them, and re-reports one whose write was lost to
    /// a restart or an unreachable server; both are one read away from being
    /// durable again. Also promotes a PR the run has since seen merged.
    ///
    /// Best-effort by construction: the view is built from the union of run and
    /// server state either way, so a failed write costs the caller nothing and
    /// the next read tries again.
    /// </summary>
    private async Task RecordUnreportedPullRequestsAsync(WorkItemServerOptions opts, RemoteWorkItem remote, IReadOnlyList<LoopRun> runs)
    {
        // Url → whether the item already records it merged, which is all this
        // needs to decide. Grouped rather than keyed directly so that an item
        // carrying the same URL twice — legacy or hand-edited data the upsert
        // would never produce — is something to shrug at, not something that
        // fails every read of that work item.
        Dictionary<string, bool>? recorded = null;

        foreach (var run in runs)
        {
            if (string.IsNullOrWhiteSpace(run.PrUrl)) continue;

            // Built on the first run that actually has a PR — most items have
            // none, and this runs per card on every taskboard poll.
            recorded ??= remote.PullRequests
                .GroupBy(p => p.Url, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Any(p => p.Merged), StringComparer.Ordinal);
            if (recorded.TryGetValue(run.PrUrl, out var knownMerged) && (knownMerged || !run.IsPrMerged))
                continue;

            bool written;
            try
            {
                written = await _server.RecordPullRequestAsync(opts, remote.Id, run.PrUrl, run.Id, run.IsPrMerged, RunPrCreatedAt(run));
            }
            catch (HttpRequestException) { written = false; }
            catch (InvalidOperationException) { return; /* No remote — local only. */ }

            // Whatever refused that write — an unreachable server, one too old
            // to know the endpoint, a lost compare-and-swap — will refuse the
            // rest of this item's just the same, so stop rather than retry it
            // per run. One attempt per read is the bound; the next read starts
            // over, which is what makes this self-healing.
            if (!written) return;
        }
    }

    /// <summary>
    /// Projects a persisted PR snapshot onto the badge-relevant subset the
    /// taskboard card renders. Returns null when there is no snapshot yet;
    /// a corrupt blob degrades to null rather than failing the whole view.
    /// </summary>
    private static WorkItemPrStatus? ResolvePrStatus(string? prSnapshot)
    {
        var snapshot = PrSnapshotJson.TryParse(prSnapshot);
        if (snapshot is null) return null;
        return new WorkItemPrStatus(
            snapshot.State,
            snapshot.Merged,
            snapshot.Mergeable,
            snapshot.MergeableState,
            snapshot.Ci,
            snapshot.Approved,
            snapshot.ChangesRequested);
    }

    /// <summary>
    /// Resolves the label of the node the run is currently on. Matches the run's
    /// CurrentNodeId against its run-node rows, preferring the most recent visit
    /// (loops can revisit a node), and falls back to the template node's label —
    /// mirroring how run nodes are surfaced elsewhere. Returns null when the run
    /// has no current node or its run nodes were not loaded.
    /// </summary>
    private static string? ResolveCurrentNodeLabel(LoopRun? run)
    {
        if (run?.CurrentNodeId is not { } currentNodeId) return null;
        var current = run.RunNodes
            .Where(rn => rn.LoopNodeId == currentNodeId)
            .OrderByDescending(rn => rn.StartedAt ?? rn.CreatedAt)
            .FirstOrDefault();
        return current?.NodeLabel ?? current?.LoopNode?.Label;
    }

    public async Task<bool> UpdateAsync(
        string workItemId,
        string title,
        string description,
        IEnumerable<string>? tags = null,
        RemoteAiProviderOverrideMode? aiProviderOverride = null,
        Guid? aiProviderOverrideId = null,
        string? branchNameOverride = null,
        string? baseBranchOverride = null,
        Guid? repositoryId = null)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var updated = await _server.UpdateAsync(opts, workItemId, new RemoteUpdateWorkItemRequest
        {
            Title = title,
            Description = description,
            Tags = tags?.ToList(),
            AiProviderOverride = aiProviderOverride,
            AiProviderOverrideId = aiProviderOverrideId,
            BranchNameOverride = branchNameOverride,
            BaseBranchOverride = baseBranchOverride,
            RepositoryId = repositoryId,
        });
        if (updated == null) return false;

        // Broadcast the edit so connected clients (e.g. the Taskboard) refresh
        // the card live instead of only on a manual page reload. An edit doesn't
        // change status, so old and new are both the current status; the
        // Taskboard's WorkItemStateChanged handler re-fetches the item, which
        // surfaces the new title/description/tags. Covers every update path —
        // UI and agent/MCP alike — since both flow through here.
        await _notifier.WorkItemStateChangedAsync(updated.Id, updated.Status, updated.Status);

        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Transitions
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A human sending an item back to Backlog — dragging the card to the
    /// Backlog column, the keyboard move, or picking Backlog from the status
    /// menu. Backlog is the full reset for re-planning, so it is reachable from
    /// every column an item can sit in before its run starts.
    ///
    /// An item with a live run behind it is refused here rather than quietly
    /// relabelled: the Active Work Item Set is derived from live runs, so a run
    /// left alive under a Backlog card would keep being heartbeated and hold a
    /// concurrency slot — the same hazard <see cref="TransitionToDoneAsync"/>
    /// guards against. Stopping that run and resetting the item is
    /// <see cref="CleanupToBacklogAsync"/>, reached from the work-item modal.
    /// </summary>
    public async Task<bool> TransitionToBacklogAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;
        if (wi.Status is not (RemoteWorkItemStatus.WorkQueue
            or RemoteWorkItemStatus.Ready
            or RemoteWorkItemStatus.HumanFeedback))
            return false;
        if (await _loopRunStore.GetActiveByWorkItemAsync(workItemId) != null) return false;
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.Backlog);
    }

    public async Task<bool> TransitionToWorkQueueAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;
        if (wi.Status != RemoteWorkItemStatus.Backlog && wi.Status != RemoteWorkItemStatus.HumanFeedback)
            return false;

        await TransitionAsync(workItemId, RemoteWorkItemStatus.WorkQueue);

        if (await IsReadyAsync(workItemId))
            await TransitionToReadyAsync(workItemId);
        return true;
    }

    public async Task<bool> TransitionToReadyAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;
        if (!await IsReadyAsync(workItemId)) return false;
        if (wi.Status != RemoteWorkItemStatus.WorkQueue && wi.Status != RemoteWorkItemStatus.Backlog)
            return false;
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.Ready);
    }

    public async Task<bool> TransitionToRunningAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;
        if (wi.Status != RemoteWorkItemStatus.Ready && wi.Status != RemoteWorkItemStatus.HumanFeedback)
            return false;
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.Running);
    }

    /// <summary>
    /// A human moving the item to HumanFeedback by hand. The reason goes on the
    /// live run when there is one, and is the item's own status reason when
    /// there is not — never onto a run that has already ended.
    /// </summary>
    public async Task<bool> TransitionToHumanFeedbackAsync(string workItemId, string reason)
    {
        if (await _loopRunStore.GetActiveByWorkItemAsync(workItemId) is not { } active)
            return await ParkWithoutRunAsync(workItemId, reason);

        await TryRecordAsync(active.Id, EventType.RunParked, reason);
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, reason,
            currentLoopRunId: active.Id);
    }

    public async Task<bool> ParkWithoutRunAsync(string workItemId, string reason)
    {
        if (await GetWorkItemAsync(workItemId) is null) return false;
        await _loopRunStore.SetWorkItemStatusReasonAsync(workItemId, reason);
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, reason);
    }


    /// <summary>
    /// A human declaring the item finished — dragging it to the Done column, or
    /// picking Done from the status menu. The run behind it is finished too, not
    /// just relabelled: the Active Work Item Set is derived from live runs, so a
    /// run left parked at a human gate under a Done card would be heartbeated
    /// and hold a concurrency slot forever. Ended before the transition, as
    /// <see cref="CleanupToDoneAsync"/> does, so nothing observes the item Done
    /// while its run still claims to be alive.
    ///
    /// The engine's own completion path is unaffected — it marks its run
    /// Completed and calls <see cref="TransitionAsync"/> directly.
    /// </summary>
    public async Task<bool> TransitionToDoneAsync(string workItemId)
    {
        var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        if (currentRun != null) await EndRunAsync(currentRun, "Work item marked Done");
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.Done,
            currentLoopRunId: currentRun?.Id ?? Guid.Empty);
    }

    public async Task<bool> TransitionAsync(
        string workItemId,
        RemoteWorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null,
        string? humanFeedbackReason = null)
    {
        var prevWi = await GetWorkItemAsync(workItemId);
        if (prevWi == null) return false;

        var prev = prevWi.Status;
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var resp = await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
        {
            TargetStatus = targetStatus,
            Actions = actions,
        });

        if (!resp.Success)
            return false;

        var actual = resp.ActualStatus;

        // Update engine-only fields on the current LoopRun. Only the run the
        // caller names is given a feedback label: one inferred from the work
        // item may be a finished run the reason has nothing to do with.
        var explicitRun = currentLoopRunId.HasValue && currentLoopRunId.Value != Guid.Empty;
        var effectiveRunId = explicitRun
            ? currentLoopRunId
            : (await _loopRunStore.GetCurrentByWorkItemAsync(workItemId))?.Id;
        string? runWorktreePath = null;
        if (effectiveRunId.HasValue)
        {
            var run = await _loopRunStore.GetByIdAsync(effectiveRunId.Value);
            if (run != null)
            {
                runWorktreePath = run.WorktreePath;
                if (actual != RemoteWorkItemStatus.HumanFeedback || explicitRun)
                {
                    run.HumanFeedbackReason = actual == RemoteWorkItemStatus.HumanFeedback && reason != null
                        // Use the dedicated humanFeedbackReason for UI routing on
                        // the LoopRun. Falls back to reason when not supplied.
                        ? humanFeedbackReason ?? reason
                        : null;
                    run.UpdatedAt = DateTime.UtcNow;
                    await _loopRunStore.UpdateRunAsync(run);
                }
            }
        }

        if (prev != actual)
            await _notifier.WorkItemStateChangedAsync(workItemId, prev, actual);

        if (actual == RemoteWorkItemStatus.HumanFeedback && reason != null)
            await _notifier.HumanFeedbackRequiredAsync(workItemId, reason);

        // Every path to Done funnels through here (the Cleanup node's
        // completion, a drag to the Done column, Cleanup-Done), so this is the
        // single place that releases the preview's ports — once Done the user
        // can no longer reach the stop control.
        if (actual == RemoteWorkItemStatus.Done)
            await StopPreviewIfRunningAsync(workItemId, runWorktreePath);

        // Wake the scheduler when a slot may have freed up (Done) or when a
        // run parked waiting for capacity might now be runnable.
        if (_scheduler != null && (actual == RemoteWorkItemStatus.Done
            || actual == RemoteWorkItemStatus.WaitingForIld
            || actual == RemoteWorkItemStatus.Ready))
            _scheduler.Pulse();

        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Dependencies (server-only)
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> AddDependencyAsync(string workItemId, string dependsOnWorkItemId)
    {
        if (workItemId == dependsOnWorkItemId)
            throw new InvalidOperationException(SelfDependencyError);

        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;

        if (await CheckNewDependencyAsync(workItemId, dependsOnWorkItemId) is { } problem)
            throw new InvalidOperationException(problem);

        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        return await _server.AddDependencyAsync(opts, workItemId, dependsOnWorkItemId);
    }

    private const string SelfDependencyError = "A work item cannot depend on itself.";

    public async Task<string?> CheckNewDependencyAsync(string workItemId, string dependsOnWorkItemId)
    {
        if (workItemId == dependsOnWorkItemId)
            return SelfDependencyError;
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        if (await _server.GetAsync(opts, dependsOnWorkItemId) == null)
            return $"Dependency not found: {dependsOnWorkItemId}";
        if (await WouldCreateCycle(workItemId, dependsOnWorkItemId))
            return $"Making {workItemId} depend on {dependsOnWorkItemId} would create a cycle.";
        return null;
    }

    public async Task<bool> RemoveDependencyAsync(string workItemId, string dependsOnWorkItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        return await _server.RemoveDependencyAsync(opts, workItemId, dependsOnWorkItemId);
    }

    public async Task<IReadOnlyList<WorkItemView>> GetDependenciesAsync(string workItemId)
    {
        var ids = await GetServerDependencyIdsAsync(workItemId);
        if (ids.Count == 0) return Array.Empty<WorkItemView>();

        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var views = new List<WorkItemView>();
        foreach (var id in ids)
        {
            var remote = await _server.GetAsync(opts, id);
            if (remote != null)
            {
                var runs = await _loopRunStore.GetAllByWorkItemAsync(id);
                var currentRun = runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
                               ?? runs.OrderByDescending(r => r.StartedAt ?? r.CreatedAt).FirstOrDefault();
                views.Add(BuildView(remote, currentRun, runs, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty),
                    await _loopRunStore.GetWorkItemStatusReasonAsync(id)));
            }
        }
        return views;
    }

    public async Task<IReadOnlyList<WorkItemView>> GetDependentsAsync(string workItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var all = await _server.ListAsync(opts, status: null, tags: null);
        var dependents = all.Where(w => w.Dependencies.Contains(workItemId)).ToList();
        var views = new List<WorkItemView>();
        foreach (var candidate in dependents)
        {
            var runs = await _loopRunStore.GetAllByWorkItemAsync(candidate.Id);
            var currentRun = runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
                           ?? runs.OrderByDescending(r => r.StartedAt ?? r.CreatedAt).FirstOrDefault();
            views.Add(BuildView(candidate, currentRun, runs, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty),
                await _loopRunStore.GetWorkItemStatusReasonAsync(candidate.Id)));
        }
        return views;
    }

    public async Task<bool> IsReadyAsync(string workItemId)
    {
        var ids = await GetServerDependencyIdsAsync(workItemId);
        if (ids.Count == 0) return true;

        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        foreach (var depId in ids)
        {
            var dep = await _server.GetAsync(opts, depId);
            if (dep == null || dep.Status != RemoteWorkItemStatus.Done)
                return false;
        }
        return true;
    }

    private async Task<IReadOnlyList<string>> GetServerDependencyIdsAsync(string workItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var serverWi = await _server.GetAsync(opts, workItemId);
        return serverWi?.Dependencies?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private async Task<bool> WouldCreateCycle(string workItemId, string newDepId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(newDepId);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!visited.Add(cur)) continue;
            if (cur == workItemId) return true;
            var nextDeps = await GetServerDependencyIdsAsync(cur);
            foreach (var n in nextDeps) stack.Push(n);
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────
    // PR / cleanup (engine-only fields on LoopRun)
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> LinkPullRequestAsync(string workItemId, string prUrl)
    {
        var run = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);

        // The link belongs to the work item, so it is recorded even when there
        // is no run to hang it on — a finished item can still have its PR
        // linked by hand. When there is a current run, it also gets the URL:
        // that is what the PR heartbeat and the PR node's re-visit path read.
        if (run != null)
        {
            run.PrUrl = prUrl;
            run.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(run);
        }

        return await RecordPullRequestAsync(workItemId, prUrl, run?.Id, merged: false,
            createdAt: run is null ? null : RunPrCreatedAt(run));
    }

    public async Task<bool> RecordPullRequestAsync(string workItemId, string prUrl, Guid? loopRunId, bool merged = false, DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(prUrl)) return false;

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            return await _server.RecordPullRequestAsync(opts, workItemId, prUrl, loopRunId, merged, createdAt);
        }
        catch (InvalidOperationException) { return false; /* No remote — local only. */ }
    }

    public async Task<bool> CleanupToDoneAsync(string workItemId)
    {
        var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;

        // The run stays inspectable until the row itself is deleted (manual
        // delete or the retention sweeper), which reclaims its worktree and
        // branch; the terminal timestamp is what makes it visible there.
        if (currentRun != null) await EndRunAsync(currentRun, "Work item cleaned up to Done");

        // Drive the Done transition through the shared path so it clears the
        // run's feedback reason, notifies clients, and stops the worktree
        // preview — a finished item can no longer be stopped from the UI.
        try
        {
            await TransitionAsync(workItemId, RemoteWorkItemStatus.Done, currentLoopRunId: currentRun?.Id ?? Guid.Empty);
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        return true;
    }

    public async Task<bool> CleanupToBacklogAsync(string workItemId)
    {
        var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);

        // Stop a still-active run but keep its worktree and branch for
        // inspection; the retention sweeper (or a manual run delete) reclaims
        // them together with the row. The next run gets its own branch and
        // worktree anyway (ADR-0008), so nothing here can leak into it.
        if (currentRun != null)
            await StopRunIfActiveAsync(currentRun, "Work item sent back to Backlog");

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Backlog,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (currentRun != null)
        {
            var endedHere = IsAlive(currentRun.Status);
            currentRun.Status = LoopRunStatus.Completed;
            // Terminal timestamp keeps the row visible to the retention
            // sweeper; without it the run is never reclaimed.
            currentRun.CompletedAt ??= DateTime.UtcNow;
            currentRun.HumanFeedbackReason = null;
            currentRun.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(currentRun);
            if (endedHere)
                await TryRecordAsync(currentRun.Id, EventType.LoopRunCompleted, "Work item sent back to Backlog");
        }

        return true;
    }

    /// <summary>
    /// The worktree, branch and repository credentials every orchestrator-run git
    /// operation on a work item's run branch needs. Resolved together because a
    /// caller holding only some of them cannot do anything useful.
    /// </summary>
    private sealed record BranchContext(WorkItemView WorkItem, string WorktreePath, string Branch, GitAuthOptions? Auth);

    /// <summary>
    /// Resolve a work item's <see cref="BranchContext"/>, or the reason it has none.
    /// Exactly one of the two is non-null.
    /// </summary>
    private async Task<(BranchContext? Context, string? Error)> ResolveBranchContextAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi is null)
            return (null, "Work item not found.");
        if (string.IsNullOrWhiteSpace(wi.WorktreePath) || !Directory.Exists(wi.WorktreePath))
            return (null, "Work item does not currently have an active worktree.");
        if (wi.RunRepositoryId is null)
            return (null, "Work item has no associated repository.");

        var repo = await _providerStore.GetRepositoryByIdAsync(wi.RunRepositoryId.Value);
        if (repo is null)
            return (null, "Repository not found.");

        var branch = wi.BranchName
            ?? (wi.CurrentLoopRunId is { } runId ? RunWorktreeNaming.BranchFor(wi.Id, runId) : null);
        if (string.IsNullOrEmpty(branch))
            return (null, "Could not resolve the work item's branch.");

        var remoteProvider = await _providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
        var gitAuth = remoteProvider is null
            ? null
            : new GitAuthOptions(repo.CloneUrl, remoteProvider.ApiKey, remoteProvider.Type);

        return (new BranchContext(wi, wi.WorktreePath, branch, gitAuth), null);
    }

    public async Task<string?> GetBranchUrlAsync(WorkItemView workItem)
    {
        // No run-id fallback for the branch, unlike ResolveBranchContextAsync:
        // the link must open the branch the Overview names.
        var branch = workItem.BranchName;
        var worktreePath = workItem.WorktreePath;
        if (string.IsNullOrWhiteSpace(branch)
            || string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath)
            || workItem.RunRepositoryId is not { } repositoryId)
            return null;

        try
        {
            var repo = await _providerStore.GetRepositoryByIdAsync(repositoryId);
            if (repo is null)
                return null;
            var remoteProvider = await _providerStore.GetRemoteProviderByIdAsync(repo.RemoteProviderId);
            if (remoteProvider is null)
                return null;
            if (!await _repoManager.RemoteBranchExistsAsync(worktreePath, branch))
                return null;

            return BranchWebUrl.For(remoteProvider.Type, repo.CloneUrl, branch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not work out the branch link for work item {WorkItemId}", workItem.Id);
            return null;
        }
    }

    public async Task<(bool Success, string? Branch, string? Error)> CommitAndPushBranchAsync(string workItemId)
    {
        var (ctx, contextError) = await ResolveBranchContextAsync(workItemId);
        if (ctx is null)
            return (false, null, contextError);

        // Mirror the PR node's prep: commit only when there is something to
        // commit, then push the branch with the repository's credentials.
        var diff = await _repoManager.GetDiffAsync(ctx.WorktreePath);
        if (!string.IsNullOrEmpty(diff) && !await _repoManager.CommitAsync(ctx.WorktreePath, ctx.WorkItem.Title))
            return (false, null, "Failed to commit uncommitted changes.");

        var pushResult = await _repoManager.PushAsync(ctx.WorktreePath, ctx.Branch, default, ctx.Auth);
        if (!pushResult.Success)
            return (false, null, $"Failed to push branch '{ctx.Branch}': {pushResult.Error ?? "unknown error"}");

        return (true, ctx.Branch, null);
    }

    public async Task<PullBranchResult> PullBranchAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        var (ctx, contextError) = await ResolveBranchContextAsync(workItemId);
        if (ctx is null)
            return new PullBranchResult(PullBranchOutcome.Failed, null, contextError!, []);

        var upstream = $"origin/{ctx.Branch}";

        // Dirty check first: it is the cheapest, and refusing before the network
        // call keeps the failure fast. Deliberately no auto-stash — a stash that
        // silently swallows an agent's in-flight edits is far worse than a refusal,
        // and CommitAndPushBranchAsync is the one-click way to clear it.
        var dirty = await _repoManager.GetUncommittedFilesAsync(ctx.WorktreePath);
        if (dirty.Count > 0)
            return new PullBranchResult(
                PullBranchOutcome.DirtyWorktree,
                ctx.Branch,
                $"Cannot pull '{ctx.Branch}': the worktree has uncommitted changes to {DescribeFiles(dirty)}. "
                + "Commit or discard them first (the Push branch action commits and pushes everything).",
                dirty);

        // The rebase below only ever touches local refs, so this fetch is the one
        // step that needs the repository's credentials — which is exactly what the
        // agent uid cannot supply for itself (ADR-0014).
        if (!await _repoManager.FetchAsync(ctx.WorktreePath, cancellationToken, ctx.Auth))
            return new PullBranchResult(
                PullBranchOutcome.Failed,
                ctx.Branch,
                "Failed to fetch origin — run Test on this repository in Repositories to see why.",
                []);

        if (!await _repoManager.RemoteBranchExistsAsync(ctx.WorktreePath, ctx.Branch))
            return new PullBranchResult(
                PullBranchOutcome.NoRemoteBranch,
                ctx.Branch,
                $"Nothing to pull: '{ctx.Branch}' has not been pushed to origin yet.",
                []);

        var behind = await _repoManager.GetCommitsBehindCountAsync(ctx.WorktreePath, upstream);
        if (behind == 0)
            return new PullBranchResult(
                PullBranchOutcome.AlreadyUpToDate,
                ctx.Branch,
                $"Already up to date with {upstream}.",
                []);

        // Rebase rather than merge, matching what the Start node does with the
        // default branch: the run branch stays a linear series of the run's own
        // commits on top of whatever origin holds.
        //
        // This rewrites far more of the working tree than the commit on the push
        // path does, and it runs as the ORCHESTRATOR inside a worktree the agent
        // uid has been writing to. That is safe without any ownership fix-up:
        // /worktrees is provisioned as a shared read/write tree (setgid + a default
        // ACL granting the shared group rwx, see entrypoint.sh), so agent-created
        // files are group-writable by the orchestrator and the files git writes
        // here come out writable by the agent — ownership differs, access does not.
        var rebase = await _repoManager.RebaseAsync(ctx.WorktreePath, upstream, cancellationToken);
        if (!rebase.Success)
        {
            // Both outcomes leave the branch untouched, but they ask different
            // things of the caller: conflicts are resolved file by file, whereas a
            // refusal (untracked files in the way, a hook, an unusable upstream) has
            // no files to resolve and only the message to act on.
            return rebase.ConflictedFiles.Count > 0
                ? new PullBranchResult(
                    PullBranchOutcome.Conflict,
                    ctx.Branch,
                    $"Rebase onto {upstream} hit conflicts in {DescribeFiles(rebase.ConflictedFiles)} and was aborted; "
                    + "the branch is unchanged. Resolve them by hand, or push this branch and reconcile on the remote.",
                    rebase.ConflictedFiles)
                : new PullBranchResult(
                    PullBranchOutcome.RebaseRefused,
                    ctx.Branch,
                    $"Git refused to rebase onto {upstream} — no conflicts to resolve, and the branch is unchanged: "
                    + (rebase.Error ?? "unknown error"),
                    []);
        }

        return new PullBranchResult(
            PullBranchOutcome.Updated,
            ctx.Branch,
            $"Rebased '{ctx.Branch}' onto {upstream}, picking up {behind} new commit{(behind == 1 ? "" : "s")}.",
            []);
    }

    // Names the files inline up to a point, then counts the rest: the message is
    // read by a human in a dialog and by an agent deciding what to do next, and a
    // rebase can conflict in hundreds of files.
    private static string DescribeFiles(IReadOnlyList<string> files)
    {
        const int shown = 10;
        return files.Count <= shown
            ? string.Join(", ", files)
            : string.Join(", ", files.Take(shown)) + $" (+{files.Count - shown} more)";
    }

    // ──────────────────────────────────────────────────────────────────
    // Human feedback
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> SubmitHumanFeedbackInputAsync(string workItemId, string input)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null || wi.CurrentLoopRunId == null) return false;

        var runId = wi.CurrentLoopRunId.Value;
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run == null) return false;

        var nodes = await _loopRunStore.GetRunNodesAsync(runId);
        var humanRunNode = FindWaitingHumanNode(nodes, run.CurrentNodeId);

        await _eventLog.AppendAsync(runId, EventType.HumanFeedbackReceived, input, humanRunNode?.LoopNodeId, humanRunNode?.Id);

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.AppendFeedbackAsync(opts, workItemId, input);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (_engine is not null && humanRunNode is not null)
        {
            await _engine.SignalNodeResultAsync(runId, humanRunNode.Id,
                NodeSignal.Success(input));
        }
        return true;
    }

    private static LoopRunNode? FindWaitingHumanNode(IReadOnlyList<LoopRunNode> nodes, Guid? currentNodeId)
    {
        var primary = nodes
            .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman && n.LoopNodeId == currentNodeId)
            .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
            .FirstOrDefault();
        if (primary != null) return primary;
        return nodes
            .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman)
            .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private async Task<bool> IsPrNodeAsync(LoopRun run, Guid loopNodeId)
    {
        if (run.LoopTemplateVersionId == Guid.Empty) return false;
        var nodes = await _loopRunStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
        var node = nodes.FirstOrDefault(n => n.Id == loopNodeId);
        return node?.NodeType == NodeType.PR;
    }

    public async Task<bool> RejectHumanFeedbackAsync(string workItemId, string? input = null)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null || wi.CurrentLoopRunId == null) return false;

        var run = await _loopRunStore.GetByIdAsync(wi.CurrentLoopRunId.Value);
        if (run == null) return false;

        var nodes = await _loopRunStore.GetRunNodesAsync(run.Id);
        var currentRunNode = FindWaitingHumanNode(nodes, run.CurrentNodeId);

        var logMessage = string.IsNullOrEmpty(input) ? "rejected by user" : $"rejected by user: {input}";
        await _eventLog.AppendAsync(run.Id, EventType.HumanFeedbackReceived, logMessage, currentRunNode?.LoopNodeId, currentRunNode?.Id);

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            if (!string.IsNullOrEmpty(input))
                await _server.AppendFeedbackAsync(opts, workItemId, $"rejected: {input}");
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (_engine is not null && currentRunNode is not null)
        {
            await _engine.SignalNodeResultAsync(run.Id, currentRunNode.Id,
                NodeSignal.Reject("Rejected by user", input));
        }
        return true;
    }

    public Task<bool> SubmitHumanFeedbackRespondAsync(string workItemId, string input)
        => SubmitHumanFeedbackEdgeAsync(workItemId, "Respond", input);

    public async Task<bool> SubmitHumanFeedbackEdgeAsync(string workItemId, string edgeName, string input)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null || wi.CurrentLoopRunId == null) return false;

        var runId = wi.CurrentLoopRunId.Value;
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run == null) return false;

        var nodes = await _loopRunStore.GetRunNodesAsync(runId);
        var humanRunNode = FindWaitingHumanNode(nodes, run.CurrentNodeId);

        await _eventLog.AppendAsync(runId, EventType.HumanFeedbackReceived, input, humanRunNode?.LoopNodeId, humanRunNode?.Id, edgeName);

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.AppendFeedbackAsync(opts, workItemId, input);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (_engine is not null && humanRunNode is not null)
        {
            await _engine.SignalNodeResultAsync(runId, humanRunNode.Id,
                NodeSignal.Custom(edgeName, input));
        }
        return true;
    }

    public async Task<MergePullRequestResult?> MergePullRequestAsync(string workItemId, bool deleteBranch)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null || wi.CurrentLoopRunId == null) return null;

        if (string.IsNullOrEmpty(wi.PrUrl))
            return new MergePullRequestResult(false, "Work item has no linked pull request.", false, null);
        if (_remoteProvider == null)
            return new MergePullRequestResult(false, "No remote provider configured.", false, null);
        if (wi.RunRepositoryId == null)
            return new MergePullRequestResult(false, "Work item has no repository.", false, null);

        var repo = await _providerStore.GetRepositoryByIdAsync(wi.RunRepositoryId.Value);
        if (repo == null)
            return new MergePullRequestResult(false, "Repository not found.", false, null);

        var prNumber = RemotePrUrl.ExtractPrNumber(wi.PrUrl);
        if (prNumber == null)
            return new MergePullRequestResult(false, $"Could not derive a PR number from '{wi.PrUrl}'.", false, null);

        var runId = wi.CurrentLoopRunId.Value;
        var merged = await _remoteProvider.MergePullRequestAsync(repo.CloneUrl, prNumber);
        if (!merged)
        {
            await TryRecordAsync(runId, EventType.PrMergeFailed, $"Merge of {wi.PrUrl} failed");
            // Leave the work item parked — do not advance the loop.
            return new MergePullRequestResult(false,
                "Failed to merge the pull request. It may have conflicts or be blocked by branch protection.",
                false, null);
        }

        await TryRecordAsync(runId, EventType.PrMerged, $"PR {wi.PrUrl} merged by user");

        // Branch deletion is best effort: a failure after a successful merge is
        // reported but never blocks loop continuation.
        var branchDeleted = false;
        string? branchWarning = null;
        if (deleteBranch)
        {
            var branch = wi.BranchName ?? RunWorktreeNaming.BranchFor(wi.Id, runId);
            branchDeleted = await _remoteProvider.DeleteBranchAsync(repo.CloneUrl, branch);
            if (!branchDeleted)
            {
                branchWarning = $"PR merged, but the branch '{branch}' could not be deleted.";
                await TryRecordAsync(runId, EventType.BranchDeleteFailed, branchWarning);
            }
        }

        // Continue along OnSuccess — identical continuation to the Approve action.
        // A run that has already ended has nothing left to continue.
        try { await SubmitHumanFeedbackInputAsync(workItemId, string.Empty); }
        catch (RunClosedException) { }

        return new MergePullRequestResult(true, null, branchDeleted, branchWarning);
    }

    public async Task<bool> DeleteAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.DeleteAsync(opts, workItemId);
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }
        await _loopRunStore.ClearWorkItemStatusReasonAsync(workItemId);

        // Delete all LoopRuns for this work item. Reclaim each run's local
        // git state first — once the rows are gone the retention sweeper can
        // never find the worktrees and branches again. A run whose reclaim
        // fails, or whose pending edit proposals cannot be withdrawn, keeps
        // its row so a later sweep retries (the work item no longer exists on
        // the server, so the sweeper's current-run guard won't protect it).
        var runs = await _loopRunStore.GetAllByWorkItemAsync(workItemId);
        foreach (var run in runs)
        {
            await StopRunIfActiveAsync(run, "Work item deleted");
            if (!await _runReclaimer.ReclaimLocalStateAsync(run))
                continue;
            try
            {
                await WithdrawPendingProposalsOfRunAsync(run.Id);
            }
            catch (HttpRequestException) { continue; }
            await _loopRunStore.DeleteAsync(run.Id);
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Attachments
    // ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RemoteWorkItemAttachment>?> ListAttachmentsAsync(string workItemId, CancellationToken ct = default)
        => await _server.ListAttachmentsAsync(await _options.ResolveForWorkItemAsync(workItemId, ct), workItemId, ct);

    public async Task<AttachmentUploadResult> AddAttachmentsAsync(string workItemId, IReadOnlyList<RemoteAttachmentUpload> files, CancellationToken ct = default)
        => await _server.UploadAttachmentsAsync(await _options.ResolveForWorkItemAsync(workItemId, ct), workItemId, files, ct);

    public async Task<(byte[] Content, string ContentType, string FileName)?> GetAttachmentAsync(string workItemId, Guid attachmentId, CancellationToken ct = default)
        => await _server.GetAttachmentAsync(await _options.ResolveForWorkItemAsync(workItemId, ct), workItemId, attachmentId, ct);

    public async Task<bool> DeleteAttachmentAsync(string workItemId, Guid attachmentId, CancellationToken ct = default)
        => await _server.DeleteAttachmentAsync(await _options.ResolveForWorkItemAsync(workItemId, ct), workItemId, attachmentId, ct);

    // ──────────────────────────────────────────────────────────────────
    // Edit proposals
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Run ids per WorkItem server query, which keeps its URL bounded.</summary>
    private const int RunIdsPerProposalQuery = 50;

    private const string WithdrawnProposalReason = "The loop run that proposed this edit was deleted.";

    public async Task<EditProposalCreateResult> ProposeEditAsync(string workItemId, RemoteCreateEditProposalRequest request, CancellationToken ct = default)
    {
        var result = await _server.CreateEditProposalAsync(await _options.ResolveForWorkItemAsync(workItemId, ct), workItemId, request, ct);
        if (result is { Outcome: EditProposalCreateOutcome.Created, Proposal: { } created })
        {
            await _notifier.WorkItemEditProposalsChangedAsync(workItemId);
            await HintRequestersAsync(workItemId, await WithRequestersAsync([created]));
        }
        return result;
    }

    public async Task<IReadOnlyList<RemoteWorkItemEditProposal>?> ListEditProposalsAsync(string workItemId, CancellationToken ct = default)
    {
        var proposals = await _server.ListEditProposalsAsync(await _options.ResolveForWorkItemAsync(workItemId, ct), workItemId, ct);
        return proposals == null ? null : await WithRequestersAsync(proposals);
    }

    public async Task<IReadOnlyList<RemoteWorkItemEditProposal>> QueryEditProposalsAsync(RemoteEditProposalQuery query, CancellationToken ct = default)
        => await WithRequestersAsync(await _server.QueryEditProposalsAsync(await _options.ResolveForRepositoryAsync(null, ct), query, ct));

    public async Task<IReadOnlyList<RemoteWorkItemEditProposal>> ListRequestedEditProposalsAsync(string workItemId, CancellationToken ct = default)
    {
        var runIds = (await _loopRunStore.GetAllByWorkItemAsync(workItemId)).Select(r => r.Id).ToList();
        if (runIds.Count == 0) return [];

        var opts = await _options.ResolveForRepositoryAsync(null, ct);
        var requested = new List<RemoteWorkItemEditProposal>();
        foreach (var chunk in runIds.Chunk(RunIdsPerProposalQuery))
            requested.AddRange(await _server.QueryEditProposalsAsync(opts, new RemoteEditProposalQuery { CreatedByLoopRunIds = chunk }, ct));
        foreach (var proposal in requested)
            proposal.RequestedByWorkItemId = workItemId;
        return requested.OrderByDescending(p => p.CreatedAt).ToList();
    }

    public async Task<EditProposalApproval> ApproveEditProposalAsync(string workItemId, Guid proposalId, CancellationToken ct = default)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId, ct);
        if (await RefuseDependencyAdditionsAsync(opts, workItemId, proposalId, ct) is { } refused)
            return refused;
        var result = await _server.ApproveEditProposalAsync(opts, workItemId, proposalId, ct);
        WorkItemView? updatedView = null;
        // An applied proposal is an edit like any other, so the board hears of it
        // the way UpdateAsync announces one.
        if (result is { Outcome: EditProposalDecisionOutcome.Applied, WorkItem: { } updated })
        {
            await _notifier.WorkItemStateChangedAsync(updated.Id, updated.Status, updated.Status);
            updatedView = await ViewOfAsync(opts, updated);
        }
        var proposal = result.Proposal is null ? null : (await WithRequestersAsync([result.Proposal]))[0];
        if (result.Outcome is EditProposalDecisionOutcome.Applied or EditProposalDecisionOutcome.Stale)
        {
            await _notifier.WorkItemEditProposalsChangedAsync(workItemId);
            if (result.Outcome == EditProposalDecisionOutcome.Applied)
                await HintRequestersOfItemsProposalsAsync(opts, workItemId, ct);
            else if (proposal is not null)
                await HintRequestersAsync(workItemId, [proposal]);
        }
        return new EditProposalApproval(result.Outcome, proposal, updatedView);
    }

    /// <summary>
    /// The WorkItem server knows nothing of cycles, so ILD checks a pending
    /// proposal's dependency additions before forwarding its approve. Only while
    /// the item still matches the snapshot: otherwise the server makes it Stale,
    /// which is the answer that wins. The check is not atomic with the write,
    /// the same as a human adding an edge.
    /// </summary>
    private async Task<EditProposalApproval?> RefuseDependencyAdditionsAsync(
        WorkItemServerOptions opts, string workItemId, Guid proposalId, CancellationToken ct)
    {
        var proposal = (await _server.ListEditProposalsAsync(opts, workItemId, ct))?.FirstOrDefault(p => p.Id == proposalId);
        if (proposal is not { Status: RemoteEditProposalStatus.Pending, Proposed.AddDependencies: { Count: > 0 } additions })
            return null;
        var item = await _server.GetAsync(opts, workItemId, ct);
        if (item is null || !StillMatchesSnapshot(item, proposal.Snapshot))
            return null;
        foreach (var addition in additions)
        {
            if (await CheckNewDependencyAsync(workItemId, addition.Id) is { } problem)
            {
                var current = (await WithRequestersAsync([proposal]))[0];
                return new EditProposalApproval(EditProposalDecisionOutcome.Refused, current, null, problem);
            }
        }
        return null;
    }

    private static bool StillMatchesSnapshot(RemoteWorkItem item, RemoteEditProposalFields snapshot)
        => item.Title == snapshot.Title
        && item.Description == snapshot.Description
        && item.Tags.SequenceEqual(snapshot.Tags ?? [])
        && item.BranchNameOverride == snapshot.BranchNameOverride
        && item.BaseBranchOverride == snapshot.BaseBranchOverride
        && (snapshot.Dependencies is null || item.Dependencies.ToHashSet().SetEquals(snapshot.Dependencies));

    public async Task<EditProposalDecisionResult> RejectEditProposalAsync(string workItemId, Guid proposalId, string? reason, CancellationToken ct = default)
    {
        var result = await _server.RejectEditProposalAsync(await _options.ResolveForWorkItemAsync(workItemId, ct), workItemId, proposalId, reason, ct);
        if (result.Proposal is null) return result;
        var proposal = (await WithRequestersAsync([result.Proposal]))[0];
        if (result.Outcome == EditProposalDecisionOutcome.Rejected)
        {
            await _notifier.WorkItemEditProposalsChangedAsync(workItemId);
            await HintRequestersAsync(workItemId, [proposal]);
        }
        return result with { Proposal = proposal };
    }

    public async Task WithdrawPendingProposalsOfRunAsync(Guid runId, CancellationToken ct = default)
    {
        WorkItemServerOptions opts;
        try { opts = await _options.ResolveForRepositoryAsync(null, ct); }
        catch (InvalidOperationException) { return; /* No remote — no proposals. */ }

        var pending = await _server.QueryEditProposalsAsync(opts, new RemoteEditProposalQuery
        {
            Status = RemoteEditProposalStatus.Pending,
            CreatedByLoopRunIds = [runId],
        }, ct);
        // A proposal decided in the meantime comes back NotPending (or NotFound
        // once its item is gone), which leaves it as it is.
        foreach (var proposal in pending)
            await RejectEditProposalAsync(proposal.WorkItemId, proposal.Id, WithdrawnProposalReason, ct);
    }

    public async Task MarkEditProposalDecisionsDeliveredAsync(IReadOnlyList<Guid> proposalIds, CancellationToken ct = default)
        => await _server.MarkEditProposalDecisionsDeliveredAsync(await _options.ResolveForRepositoryAsync(null, ct), proposalIds, ct);

    /// <summary>
    /// Names the work item whose loop run made each proposal, read from the run
    /// itself. A run that no longer exists names nothing, and neither does a chat.
    /// </summary>
    private async Task<IReadOnlyList<RemoteWorkItemEditProposal>> WithRequestersAsync(IReadOnlyList<RemoteWorkItemEditProposal> proposals)
    {
        var requesterOfRun = new Dictionary<Guid, string?>();
        foreach (var proposal in proposals)
        {
            if (proposal.CreatedByLoopRunId is not { } runId) continue;
            if (!requesterOfRun.TryGetValue(runId, out var requester))
                requesterOfRun[runId] = requester = (await _loopRunStore.GetByIdAsync(runId))?.WorkItemId;
            proposal.RequestedByWorkItemId = requester;
        }
        return proposals;
    }

    /// <summary>The requesting items show these proposals in their Action tab, so they re-read too.</summary>
    private async Task HintRequestersAsync(string workItemId, IEnumerable<RemoteWorkItemEditProposal> proposals)
    {
        foreach (var requester in proposals.Select(p => p.RequestedByWorkItemId).OfType<string>().Distinct())
            if (requester != workItemId)
                await _notifier.WorkItemEditProposalsChangedAsync(requester);
    }

    /// <summary>
    /// An applied approve turns the item's other pending proposals Stale, so every
    /// item that asked for one re-reads. The approve has already landed: failing to
    /// read who to tell must not report it as failed, and a requester that misses
    /// the hint re-reads on its next reconnect.
    /// </summary>
    private async Task HintRequestersOfItemsProposalsAsync(WorkItemServerOptions opts, string workItemId, CancellationToken ct)
    {
        IReadOnlyList<RemoteWorkItemEditProposal>? proposals;
        try { proposals = await _server.ListEditProposalsAsync(opts, workItemId, ct); }
        catch (HttpRequestException) { return; }
        if (proposals != null)
            await HintRequestersAsync(workItemId, await WithRequestersAsync(proposals));
    }

    // ──────────────────────────────────────────────────────────────────
    // Mapping helpers
    // ──────────────────────────────────────────────────────────────────

    internal static RemoteWorkItemStatus MapToRemote(WorkItemStatus s) => s switch
    {
        WorkItemStatus.Backlog => RemoteWorkItemStatus.Backlog,
        WorkItemStatus.WorkQueue => RemoteWorkItemStatus.WorkQueue,
        WorkItemStatus.Ready => RemoteWorkItemStatus.Ready,
        WorkItemStatus.Running => RemoteWorkItemStatus.Running,
        WorkItemStatus.HumanFeedback => RemoteWorkItemStatus.HumanFeedback,
        WorkItemStatus.WaitingForIld => RemoteWorkItemStatus.WaitingForIld,
        WorkItemStatus.Done => RemoteWorkItemStatus.Done,
        _ => RemoteWorkItemStatus.Backlog,
    };

    internal static WorkItemStatus MapFromRemote(RemoteWorkItemStatus s) => s switch
    {
        RemoteWorkItemStatus.Backlog => WorkItemStatus.Backlog,
        RemoteWorkItemStatus.WorkQueue => WorkItemStatus.WorkQueue,
        RemoteWorkItemStatus.Ready => WorkItemStatus.Ready,
        RemoteWorkItemStatus.Running => WorkItemStatus.Running,
        RemoteWorkItemStatus.HumanFeedback => WorkItemStatus.HumanFeedback,
        RemoteWorkItemStatus.WaitingForIld => WorkItemStatus.WaitingForIld,
        RemoteWorkItemStatus.Done => WorkItemStatus.Done,
        _ => WorkItemStatus.Backlog,
    };
}
