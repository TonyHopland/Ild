using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using System.Text.Json;

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
        IRemoteProvider? remoteProvider = null)
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
        _runReclaimer = runReclaimer ?? new RunReclaimer(repoManager, providerStore);
        _remoteProvider = remoteProvider;
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
        if (run.Status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman)
            run.Status = LoopRunStatus.Cancelled;
        run.CompletedAt ??= DateTime.UtcNow;
        await _loopRunStore.UpdateRunAsync(run);
    }

    /// <summary>
    /// Stop a still-active run, leaving its work item's status to the caller.
    /// Does <b>not</b> touch the run's worktree or branch — local git state
    /// lives exactly as long as the run row and is reclaimed only when the run
    /// itself is deleted (manual delete or the retention sweeper).
    ///
    /// <see cref="ILoopEngine.StopRunAsync"/> and not <c>CancelRunAsync</c>:
    /// the latter parks the work item in HumanFeedback, and every caller here
    /// is on its way to giving the item a status of its own. Inheriting that
    /// park would leave a "Run cancelled" message in the item's conversation
    /// and pop a needs-attention toast on the card the human just finished —
    /// both outliving the status written over it a moment later.
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
        Guid? createdByChatSessionId = null)
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
        var remote = await _server.GetAsync(await _options.ResolveForWorkItemAsync(workItemId), workItemId);
        if (remote == null) return null;

        var runs = await _loopRunStore.GetAllByWorkItemAsync(workItemId);
        var currentRun = runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
                       ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.WaitingHuman)
                       ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.Failed)
                       ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.Cancelled)
                       ?? runs.Where(r => r.Status != LoopRunStatus.Completed)
                              .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
                              .FirstOrDefault();

        var timingRun = LatestRun(runs);
        return BuildView(remote, currentRun, timingRun, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty));
    }

    public async Task<IReadOnlyList<WorkItemView>> ListAsync(
        RemoteWorkItemStatus? status,
        Guid? createdByLoopRunId,
        Guid? repositoryId,
        int skip,
        int take)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;

        var opts = await _options.ResolveForRepositoryAsync(repositoryId);
        var remoteList = await _server.ListAsync(opts, status, tags: null);
        if (remoteList.Count == 0) return Array.Empty<WorkItemView>();

        var allRuns = await _loopRunStore.GetAllAsync(skip: 0, take: int.MaxValue);
        var runsByWorkItem = allRuns.GroupBy(r => r.WorkItemId).ToDictionary(g => g.Key, g => g.ToList());

        var views = new List<WorkItemView>();
        foreach (var r in remoteList)
        {
            LoopRun? currentRun = null;
            LoopRun? timingRun = null;
            if (runsByWorkItem.TryGetValue(r.Id, out var runs))
            {
                currentRun = runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.Running)
                           ?? runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.WaitingHuman)
                           ?? runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.Failed)
                           ?? runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.Cancelled)
                           ?? runs.Where(rn => rn.Status != LoopRunStatus.Completed)
                                  .OrderByDescending(rn => rn.StartedAt ?? rn.CreatedAt)
                                  .FirstOrDefault();
                timingRun = LatestRun(runs);
            }
            var view = BuildView(r, currentRun, timingRun, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty));
            if (createdByLoopRunId.HasValue && view.CreatedByLoopRunId != createdByLoopRunId) continue;
            if (repositoryId.HasValue && view.RepositoryId != repositoryId) continue;
            views.Add(view);
        }

        return views
            .OrderByDescending(v => v.CreatedAt)
            .Skip(skip)
            .Take(take)
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

        IEnumerable<RemoteWorkItem> filtered = all;
        if (query.Status.HasValue) filtered = filtered.Where(w => w.Status == query.Status.Value);
        if (query.Priority.HasValue) filtered = filtered.Where(w => w.Priority == query.Priority.Value);
        if (query.RepositoryId.HasValue) filtered = filtered.Where(w => w.RepositoryId == query.RepositoryId.Value);
        if (query.CreatedByLoopRunId.HasValue) filtered = filtered.Where(w => w.CreatedByLoopRunId == query.CreatedByLoopRunId.Value);
        if (query.Tags is { Count: > 0 })
        {
            var wanted = query.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(w => w.Tags.Any(wanted.Contains));
        }
        if (query.ActionableOnly)
            filtered = filtered.Where(w => IsActionable(w, statusById));

        var ordered = query.OrderBy switch
        {
            WorkItemOrderBy.Priority => filtered.OrderByDescending(w => w.Priority).ThenByDescending(w => w.UpdatedAt),
            WorkItemOrderBy.CreatedAt => filtered.OrderByDescending(w => w.CreatedAt),
            _ => filtered.OrderByDescending(w => w.UpdatedAt),
        };

        var skip = query.Skip < 0 ? 0 : query.Skip;
        var take = query.Take <= 0 ? 100 : query.Take;
        return ordered
            .Skip(skip)
            .Take(take)
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

    private static WorkItemView BuildView(RemoteWorkItem remote, LoopRun? run, LoopRun? timingRun, bool isPreviewRunning)
    {
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
            Conversation = remote.Conversation,
            HumanFeedbackActions = remote.HumanFeedbackActions,
            AiProviderOverride = remote.AiProviderOverride,
            AiProviderOverrideId = remote.AiProviderOverrideId,
            RepositoryId = run?.RepositoryId ?? remote.RepositoryId,
            CreatedByLoopRunId = run?.CreatedByLoopRunId ?? remote.CreatedByLoopRunId,
            CreatedByChatSessionId = remote.CreatedByChatSessionId,
            StartedAt = timingRun?.StartedAt,
            CompletedAt = timingRun?.CompletedAt,
            WorktreePath = run?.WorktreePath,
            BranchName = run?.BranchName,
            PrUrl = run?.PrUrl,
            IsPrMerged = run?.IsPrMerged == true,
            HumanFeedbackReason = run?.HumanFeedbackReason,
            CurrentLoopRunId = run?.Id,
            CurrentNodeLabel = ResolveCurrentNodeLabel(run),
            IsPreviewRunning = isPreviewRunning,
            PrStatus = ResolvePrStatus(run),
        };
    }

    // The poller persists the snapshot with the web (camelCase) options, so
    // deserialize with the same to round-trip the property names and the
    // string-named CI enum.
    private static readonly JsonSerializerOptions PrSnapshotJson = JsonSerializerOptions.Web;

    /// <summary>
    /// Projects the run's persisted PR snapshot onto the badge-relevant subset
    /// the taskboard card renders. Returns null when there is no snapshot yet;
    /// a corrupt blob degrades to null rather than failing the whole view.
    /// </summary>
    private static WorkItemPrStatus? ResolvePrStatus(LoopRun? run)
    {
        if (string.IsNullOrEmpty(run?.PrSnapshot)) return null;
        RemotePrSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<RemotePrSnapshot>(run.PrSnapshot, PrSnapshotJson);
        }
        catch (JsonException)
        {
            return null;
        }
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
        Guid? aiProviderOverrideId = null)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var updated = await _server.UpdateAsync(opts, workItemId, new RemoteUpdateWorkItemRequest
        {
            Title = title,
            Description = description,
            Tags = tags?.ToList(),
            AiProviderOverride = aiProviderOverride,
            AiProviderOverrideId = aiProviderOverrideId,
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

    public Task<bool> TransitionToHumanFeedbackAsync(string workItemId, string reason)
        => TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, reason);

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
        string? humanFeedbackReason = null,
        string? name = null)
    {
        var prevWi = await GetWorkItemAsync(workItemId);
        if (prevWi == null) return false;

        var prev = prevWi.Status;
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var resp = await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
        {
            TargetStatus = targetStatus,
            Reason = reason,
            Actions = actions,
            Name = name,
        });

        if (!resp.Success)
            return false;

        var actual = resp.ActualStatus;

        // Update engine-only fields on the current LoopRun
        Guid? effectiveRunId = currentLoopRunId;
        if (!effectiveRunId.HasValue || effectiveRunId.Value == Guid.Empty)
        {
            var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
            effectiveRunId = currentRun?.Id;
        }
        string? runWorktreePath = null;
        if (effectiveRunId.HasValue)
        {
            var run = await _loopRunStore.GetByIdAsync(effectiveRunId.Value);
            if (run != null)
            {
                runWorktreePath = run.WorktreePath;
                if (actual == RemoteWorkItemStatus.HumanFeedback && reason != null)
                {
                    // Use the dedicated humanFeedbackReason for UI routing on
                    // the LoopRun. Falls back to reason when not supplied.
                    run.HumanFeedbackReason = humanFeedbackReason ?? reason;
                }
                else
                    run.HumanFeedbackReason = null;
                run.UpdatedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunAsync(run);
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

    public async Task<bool> AppendAiTurnAsync(string workItemId, string name, string content)
    {
        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            return await _server.AppendConversationAsync(opts, workItemId, "ai", content, name);
        }
        catch (InvalidOperationException) { return false; /* No remote — local only. */ }
    }

    // ──────────────────────────────────────────────────────────────────
    // Dependencies (server-only)
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> AddDependencyAsync(string workItemId, string dependsOnWorkItemId)
    {
        if (workItemId == dependsOnWorkItemId)
            throw new InvalidOperationException("A work item cannot depend on itself.");

        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;

        if (await WouldCreateCycle(workItemId, dependsOnWorkItemId))
            throw new InvalidOperationException("Adding this dependency would create a cycle.");

        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        return await _server.AddDependencyAsync(opts, workItemId, dependsOnWorkItemId);
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
                views.Add(BuildView(remote, currentRun, LatestRun(runs), _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty)));
            }
        }
        return views;
    }

    public async Task<IReadOnlyList<WorkItemView>> GetDependentsAsync(string workItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var all = await _server.ListAsync(opts, status: null, tags: null);
        var views = new List<WorkItemView>();
        foreach (var candidate in all)
        {
            if (candidate.Dependencies.Contains(workItemId))
            {
                var runs = await _loopRunStore.GetAllByWorkItemAsync(candidate.Id);
                var currentRun = runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
                               ?? runs.OrderByDescending(r => r.StartedAt ?? r.CreatedAt).FirstOrDefault();
                views.Add(BuildView(candidate, currentRun, LatestRun(runs), _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty)));
            }
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
        if (run == null) return false;
        run.PrUrl = prUrl;
        run.UpdatedAt = DateTime.UtcNow;
        await _loopRunStore.UpdateRunAsync(run);
        return true;
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
            currentRun.Status = LoopRunStatus.Completed;
            // Terminal timestamp keeps the row visible to the retention
            // sweeper; without it the run is never reclaimed.
            currentRun.CompletedAt ??= DateTime.UtcNow;
            currentRun.HumanFeedbackReason = null;
            currentRun.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(currentRun);
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
        if (wi.RepositoryId is null)
            return (null, "Work item has no associated repository.");

        var repo = await _providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value);
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
                "Failed to fetch origin — check the repository's credentials and connectivity.",
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

        await _eventLog.AppendAsync(runId, "HumanFeedbackReceived", input);

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
        await _eventLog.AppendAsync(run.Id, "HumanFeedbackReceived", logMessage);

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

        await _eventLog.AppendAsync(runId, "HumanFeedbackReceived", input);

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
        if (wi.RepositoryId == null)
            return new MergePullRequestResult(false, "Work item has no repository.", false, null);

        var repo = await _providerStore.GetRepositoryByIdAsync(wi.RepositoryId.Value);
        if (repo == null)
            return new MergePullRequestResult(false, "Repository not found.", false, null);

        var prNumber = RemotePrUrl.ExtractPrNumber(wi.PrUrl);
        if (prNumber == null)
            return new MergePullRequestResult(false, $"Could not derive a PR number from '{wi.PrUrl}'.", false, null);

        var runId = wi.CurrentLoopRunId.Value;
        var merged = await _remoteProvider.MergePullRequestAsync(repo.CloneUrl, prNumber);
        if (!merged)
        {
            await _eventLog.AppendAsync(runId, "PrMergeFailed", $"Merge of {wi.PrUrl} failed");
            // Leave the work item parked — do not advance the loop.
            return new MergePullRequestResult(false,
                "Failed to merge the pull request. It may have conflicts or be blocked by branch protection.",
                false, null);
        }

        await _eventLog.AppendAsync(runId, "PrMerged", $"PR {wi.PrUrl} merged by user");

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
                await _eventLog.AppendAsync(runId, "BranchDeleteFailed", branchWarning);
            }
        }

        // Continue along OnSuccess — identical continuation to the Approve action.
        await SubmitHumanFeedbackInputAsync(workItemId, string.Empty);

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

        // Delete all LoopRuns for this work item. Reclaim each run's local
        // git state first — once the rows are gone the retention sweeper can
        // never find the worktrees and branches again. A run whose reclaim
        // fails keeps its row so a later sweep retries (the work item no
        // longer exists on the server, so the sweeper's current-run guard
        // won't protect it).
        var runs = await _loopRunStore.GetAllByWorkItemAsync(workItemId);
        foreach (var run in runs)
        {
            await StopRunIfActiveAsync(run, "Work item deleted");
            if (!await _runReclaimer.ReclaimLocalStateAsync(run))
                continue;
            await _loopRunStore.DeleteAsync(run.Id);
        }
        return true;
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
