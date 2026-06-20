using System.Collections.Concurrent;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Sole state-machine for loop runs. Drives <see cref="INodeExecutor"/>
/// generators, performs all persistence (LoopRun, LoopRunNode, EventLog),
/// fires SignalR notifications, and routes via outgoing edges. Executors
/// are pure I/O — they yield <see cref="NodeOutcome"/> values; the engine
/// interprets them.
/// </summary>
public sealed class LoopEngine : ILoopEngine
{
    private readonly IServiceProvider _sp;
    private readonly INodeExecutorRegistry _registry;
    private readonly IRunNotifier _notifier;
    private readonly IRunProgressBuffer _progressBuffer;
    private readonly IWorkItemNotifier _workItemNotifier;
    private readonly ILogger<LoopEngine> _logger;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runCts = new();
    private readonly ConcurrentDictionary<Guid, Task> _runTasks = new();

    /// <summary>Fallback maximum number of times an edge can be traversed within
    /// a single run when the edge's own <c>MaxTraversals</c> is null. Counted
    /// in-memory per run on a per-edge basis.</summary>
    public const int DefaultMaxEdgeTraversals = 50;

    public LoopEngine(
        IServiceProvider sp,
        INodeExecutorRegistry registry,
        IRunNotifier notifier,
        ILogger<LoopEngine> logger,
        IWorkItemNotifier? workItemNotifier = null,
        IRunProgressBuffer? progressBuffer = null)
    {
        _sp = sp;
        _registry = registry;
        _notifier = notifier;
        _logger = logger;
        _workItemNotifier = workItemNotifier ?? new NoopWorkItemNotifier();
        _progressBuffer = progressBuffer ?? new RunProgressBuffer();
    }

    public async Task StartRunAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var templateStore = sp.GetRequiredService<ILoopTemplateStore>();

        var wi = await workItems.GetWorkItemAsync(workItemId)
            ?? throw new InvalidOperationException($"WorkItem '{workItemId}' not found");

        // At-most-one-active-run invariant: never start a second run while one is
        // already alive (Running or parked WaitingHuman) for this work item. A
        // duplicate would drive the same work item — and its worktree/branch —
        // concurrently. This is the safety net for races where the same item is
        // claimed twice (e.g. the stale-heartbeat reclaimer flipping a parked
        // run's item back to Ready after a human resumes it). The existing run
        // already owns the item, so this is a silent no-op.
        var existingActive = await loopRunStore.GetActiveByWorkItemAsync(workItemId);
        if (existingActive is not null)
        {
            _logger.LogWarning(
                "StartRunAsync skipped for work item {WorkItemId}: run {RunId} is already active ({Status})",
                workItemId, existingActive.Id, existingActive.Status);
            return;
        }

        var resolver = sp.GetRequiredService<Remote.ILoopTemplateResolver>();
        var resolution = resolver.Resolve(wi.Tags);
        if (resolution.Kind != Remote.LoopTemplateResolutionKind.Single || resolution.TemplateId is null)
        {
            var reason = resolution.Kind == Remote.LoopTemplateResolutionKind.None
                ? "No loop found for existing tags"
                : $"Multiple loop templates match tags: {string.Join(", ", resolution.MatchingTemplateNames)}";
            await workItems.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, reason);
            return;
        }
        // Park instead of throwing for a broken template: an exception here
        // leaves the work item claimed as Running on the server with no run
        // driving it — stuck and holding a concurrency slot forever.
        var template = await templateStore.GetByIdAsync(resolution.TemplateId.Value);
        var version = await templateStore.GetLatestVersionAsync(resolution.TemplateId.Value);
        if (version is null)
        {
            await workItems.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback,
                "Matched loop template has no version");
            return;
        }
        var startNode = await loopRunStore.GetStartNodeAsync(version.Id);
        if (startNode is null)
        {
            await workItems.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback,
                "Matched loop template has no Start node");
            return;
        }

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = startNode.Id,
            RepositoryId = wi.RepositoryId,
            // The per-template setting controls crash recovery; pin it on the
            // run the same way the template version is pinned.
            RecoveryPolicy = template?.RecoveryPolicy ?? RecoveryPolicy.AutoResume,
        };
        await loopRunStore.CreateRunAsync(run);
        await workItems.TransitionAsync(workItemId, RemoteWorkItemStatus.Running, currentLoopRunId: run.Id);
        await _notifier.RunStateChangedAsync(run.Id, LoopRunStatus.Running, LoopRunStatus.Running);

        _ = LaunchAsync(run.Id);
    }

    public async Task ResumeRecoveredRunAsync(Guid runId)
    {
        // If a loop already owns this run, recovery must be a no-op: marking
        // its in-flight nodes Interrupted (below) would corrupt a live
        // execution, and relaunching is already blocked by the single-owner
        // gate. This is what makes the periodic scheduler's WaitingForIld
        // resume calls — and the stuck-run watchdog — safe to invoke against a
        // run that may genuinely be mid-node.
        if (_runCts.ContainsKey(runId))
        {
            _logger.LogDebug("ResumeRecoveredRunAsync skipped for run {RunId}: already owned", runId);
            return;
        }
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var staleNodes = (await store.GetRunNodesAsync(runId))
                .Where(rn => rn.Status == LoopRunNodeStatus.Running)
                .ToList();
            foreach (var rn in staleNodes)
            {
                rn.Status = LoopRunNodeStatus.Interrupted;
                rn.CompletedAt = DateTime.UtcNow;
                await store.UpdateRunNodeAsync(rn);
                await _notifier.NodeStateChangedAsync(runId, rn.LoopNodeId,
                    LoopRunNodeStatus.Running, LoopRunNodeStatus.Interrupted);
            }
        }
        _ = LaunchAfterAwaitAsync(runId);
    }

    public async Task ResumeRunAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var workItems = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;
        if (run.Status == LoopRunStatus.WaitingHuman) return; // requires signal
        run.IsPaused = false;
        await loopRunStore.UpdateRunAsync(run);
        await _notifier.ResumedAsync(runId);
        if (run.Status == LoopRunStatus.Running)
            _ = LaunchAfterAwaitAsync(runId);
    }

    public async Task PauseRunAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;
        run.IsPaused = true;
        await loopRunStore.UpdateRunAsync(run);
        if (_runCts.TryGetValue(runId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        await _notifier.PausedAsync(runId);
    }

    public async Task CancelRunAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var workItems = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;
        var old = run.Status;
        run.Status = LoopRunStatus.Cancelled;
        run.CompletedAt = DateTime.UtcNow;
        await loopRunStore.UpdateRunAsync(run);
        _progressBuffer.Clear(runId);
        if (_runCts.TryGetValue(runId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        await _notifier.RunStateChangedAsync(runId, old, LoopRunStatus.Cancelled);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            reason: HumanFeedbackReasons.RunCancelled, humanFeedbackReason: HumanFeedbackReasons.RunCancelled);
    }

    public async Task HaltRunAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var templateStore = sp.GetRequiredService<ILoopTemplateStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;

        // Halt only applies to a run actively executing an AI node. The
        // status + node-type checks together no-op the race where the node
        // completes (or the engine advances off the AI node) between the user
        // clicking Halt and this handler running.
        if (run.Status != LoopRunStatus.Running || run.CurrentNodeId is null) return;
        var nodes = await templateStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
        var node = nodes.FirstOrDefault(n => n.Id == run.CurrentNodeId.Value);
        if (node is null || node.NodeType != NodeType.AI) return;

        var old = run.Status;
        run.Status = LoopRunStatus.WaitingHuman;
        run.IsHalted = true;
        run.HumanFeedbackReason = HumanFeedbackReasons.RunHalted;
        await loopRunStore.UpdateRunAsync(run);

        // Cancel the run's CTS to kill the in-flight agent process now. The
        // executor surfaces the cancellation as a Fail/OperationCanceled the
        // engine's existing cancellation path records as an Interrupted node,
        // without routing or clobbering the WaitingHuman status set above.
        if (_runCts.TryGetValue(runId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        await _notifier.RunStateChangedAsync(runId, old, LoopRunStatus.WaitingHuman);
        await _notifier.HaltedAsync(runId);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            reason: HumanFeedbackReasons.RunHalted, humanFeedbackReason: HumanFeedbackReasons.RunHalted,
            currentLoopRunId: run.Id, name: node.Label);
    }

    public async Task ResumeFromHaltAsync(Guid runId, string? note)
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;
        // Only a halted, parked run can be steered/resumed here.
        if (run.Status != LoopRunStatus.WaitingHuman || !run.IsHalted) return;

        var old = run.Status;
        // A non-null note (empty allowed) flags the AI node to continue the
        // captured session; the executor consumes and clears it one-shot.
        run.SteeringNote = note ?? string.Empty;
        run.IsHalted = false;
        run.HumanFeedbackReason = null;
        run.Status = LoopRunStatus.Running;
        await loopRunStore.UpdateRunAsync(run);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.Running, currentLoopRunId: run.Id);
        await _notifier.RunStateChangedAsync(runId, old, LoopRunStatus.Running);
        _ = LaunchAfterAwaitAsync(runId);
    }

    public async Task<LoopRunStatus?> GetRunStatusAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<ILoopRunStore>().GetByIdAsync(runId);
        return run?.Status;
    }

    // Report liveness off _runCts, not _runTasks: the CTS entry is the
    // ownership token (added before the driving task is scheduled, removed
    // last in its finally), so "active" here means exactly "owned by a live
    // loop". Keying off _runTasks instead would leave a window — between the
    // ownership claim and the task handle being stored, and again between the
    // two removals in finally — where a genuinely-driven run looks inactive.
    // The stuck-run watchdog relies on this equivalence to never recover a run
    // that a loop still owns.
    public Task<IEnumerable<Guid>> GetActiveRunIdsAsync() =>
        Task.FromResult<IEnumerable<Guid>>(_runCts.Keys.ToArray());

    public async Task SignalNodeResultAsync(Guid runId, Guid runNodeId, NodeSignal signal)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;
        if (run.Status != LoopRunStatus.WaitingHuman)
        {
            _logger.LogWarning("SignalNodeResult rejected: run {RunId} not WaitingHuman (status={Status})", runId, run.Status);
            return;
        }

        // Validate that the target run node belongs to this run and is in WaitingHuman status.
        var targetNode = await loopRunStore.GetRunNodeByIdAsync(runNodeId);
        if (targetNode is null || targetNode.LoopRunId != runId || targetNode.Status != LoopRunNodeStatus.WaitingHuman)
        {
            _logger.LogWarning("SignalNodeResult rejected: runNode {RunNodeId} not a WaitingHuman node for run {RunId}", runNodeId, runId);
            return;
        }

        run.ExternalActionResult = signal.Output ?? signal.Error ?? string.Empty;
        run.ExternalActionResultType = signal.Type;
        run.ExternalActionEdgeName = signal.EdgeName;
        // Clear the parked reason now that the human has responded; otherwise the
        // stale reason keeps the "Human Input Needed" badge visible in the running
        // view (the badge keys off HumanFeedbackReason, not status) until some
        // later transition happens to null it. Mirrors ResumeFromHaltAsync. The
        // field is a transient "currently parked" pointer for display only — its
        // sole reader is WorkItemManager.BuildView; nothing routes on it, the
        // interaction history lives in the EventLog, and the next park writes a
        // fresh reason — so nulling it here loses nothing.
        run.HumanFeedbackReason = null;
        var old = run.Status;
        run.Status = LoopRunStatus.Running;
        await loopRunStore.UpdateRunAsync(run);
        await _notifier.RunStateChangedAsync(runId, old, LoopRunStatus.Running);
        _ = LaunchAfterAwaitAsync(runId);
    }

    public async Task RetryFromNodeAsync(Guid runId, Guid runNodeId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var workItems = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;
        if (run.Status == LoopRunStatus.Running && !run.IsPaused)
            throw new InvalidOperationException("Cannot retry while run is actively executing");
        var target = await loopRunStore.GetRunNodeByIdAsync(runNodeId);
        if (target is null || target.LoopRunId != runId) return;

        // Walk the chain to find the predecessor's output to use as PreviousNodeOutput.
        string? prevOutput = null;
        if (target.PreviousNodeId is Guid prevId)
        {
            var prev = await loopRunStore.GetRunNodeByIdAsync(prevId);
            prevOutput = prev?.Output;
        }
        var oldStatus = run.Status;
        run.CurrentNodeId = target.LoopNodeId;
        run.PreviousNodeOutput = prevOutput;
        run.ExternalActionResult = null;
        run.ExternalActionResultType = ExternalActionResultType.Success;
        run.ExternalActionEdgeName = null;
        run.Status = LoopRunStatus.Running;
        run.IsPaused = false;
        await loopRunStore.UpdateRunAsync(run);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.Running, currentLoopRunId: run.Id);
        await _notifier.RunStateChangedAsync(runId, oldStatus, LoopRunStatus.Running);
        _ = LaunchAfterAwaitAsync(runId);
    }

    public async Task CleanupRunAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var templateStore = sp.GetRequiredService<ILoopTemplateStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run is null) return;
        var nodes = await templateStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
        var cleanup = nodes.FirstOrDefault(n => n.NodeType == NodeType.Cleanup);
        if (cleanup is null) return;
        run.CurrentNodeId = cleanup.Id;
        await loopRunStore.UpdateRunAsync(run);
        await ExecuteSingleNodeAsync(run, cleanup, sp, CancellationToken.None, new Dictionary<Guid, int>());
    }

    // ----- Core loop -----

    private async Task LaunchAfterAwaitAsync(Guid runId)
    {
        // If a previous loop is still finalising (e.g. a Pause→Resume race or
        // a webhook firing on the trailing edge of a parked run), wait for it
        // to exit before starting a new one. This avoids duplicate LoopRunNode
        // rows when two loops would otherwise race on the same run.
        if (_runTasks.TryGetValue(runId, out var existing) && !existing.IsCompleted)
        {
            try { await existing.ConfigureAwait(false); } catch { }
        }
        await LaunchAsync(runId).ConfigureAwait(false);
    }

    private Task LaunchAsync(Guid runId)
    {
        // Single-owner gate. The CancellationTokenSource entry IS the ownership
        // token: whoever wins this atomic TryAdd owns the run's driving loop for
        // its entire lifetime (the entry is removed only in the task's finally
        // below), so a concurrent caller can never start a second loop on the
        // same run. Two concurrent loops would otherwise produce duplicated
        // LoopRunNode rows and corrupt visit counts. A prior check-then-add on
        // _runTasks left a TOCTOU window between the check and this add; folding
        // both into the single atomic TryAdd removes it.
        var cts = new CancellationTokenSource();
        if (!_runCts.TryAdd(runId, cts))
        {
            cts.Dispose();
            _logger.LogDebug("LaunchAsync skipped for run {RunId}: already owned", runId);
            return Task.CompletedTask;
        }
        var task = Task.Run(async () =>
        {
            try { await RunUntilParkAsync(runId, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run {RunId} crashed", runId);
                // Without this the run is left in the DB as Running forever with
                // no task driving it: the work item hangs in the Running column
                // and the run page shows RUNNING even though nothing will ever
                // resume it. Park it for human review instead. (StuckRunWatchdog
                // is the backstop for the exit paths this catch can't see.)
                await TrySafe(() => MarkRunCrashedAsync(runId, ex.Message));
            }
            finally
            {
                // Remove the task handle first, then release ownership: a
                // relaunch awaiting this task (LaunchAfterAwaitAsync) only
                // re-claims after the CTS entry is gone, so its new loop can
                // never overlap this one.
                _runTasks.TryRemove(runId, out _);
                _runCts.TryRemove(runId, out _);
                cts.Dispose();
            }
        });
        _runTasks[runId] = task;
        return Task.CompletedTask;
    }

    private async Task RunUntilParkAsync(Guid runId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var templateStore = sp.GetRequiredService<ILoopTemplateStore>();
        var edgeTraversalCount = await RebuildEdgeTraversalCountsAsync(loopRunStore, runId);

        while (!ct.IsCancellationRequested)
        {
            var run = await loopRunStore.GetByIdAsync(runId);
            if (run is null) return;
            // On repeat iterations GetByIdAsync returns the already-tracked
            // instance with its in-memory values (EF identity resolution), so
            // pause/cancel/pin written by other scopes since the previous
            // iteration would be invisible without an explicit reload.
            try { await loopRunStore.ReloadAsync(run); } catch { return; }
            if (run.Status != LoopRunStatus.Running || run.IsPaused) return;
            if (run.CurrentNodeId is null) return;

            var nodes = await templateStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
            var node = nodes.FirstOrDefault(n => n.Id == run.CurrentNodeId.Value);
            if (node is null) { _logger.LogError("Node {Id} missing", run.CurrentNodeId); return; }

            var park = await ExecuteSingleNodeAsync(run, node, sp, ct, edgeTraversalCount);
            if (park is ParkResult.Stop) return;
        }
    }

    private enum ParkResult { Continue, Stop }

    private async Task<ParkResult> ExecuteSingleNodeAsync(
        LoopRun run, LoopNode node, IServiceProvider sp, CancellationToken ct,
        Dictionary<Guid, int> edgeTraversalCount)
    {
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var templateStore = sp.GetRequiredService<ILoopTemplateStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var eventLog = sp.GetService<IEventLogService>();
        var executor = _registry.Get(node.NodeType);
        // Status at entry: Running for the normal loop, but CleanupRunAsync
        // drives this method against already-terminal runs too. The reload
        // guard below bails when the status *changed* underneath us.
        var entryStatus = run.Status;
        // On re-entry after signal (ExternalActionResult set), reuse the existing
        // WaitingHuman run node so CompleteRunNodeAsync updates it instead of
        // silently skipping (runNodeId == null).
        Guid? runNodeId = null;
        if (run.ExternalActionResult is not null)
        {
            var existing = (await loopRunStore.GetRunNodesAsync(run.Id))
                .FirstOrDefault(rn => rn.LoopNodeId == node.Id && rn.Status == LoopRunNodeStatus.WaitingHuman);
            runNodeId = existing?.Id;
        }

        // Capture the complete live stream into the per-run buffer (which both
        // backs the mid-run replay and assigns the sequence number) before
        // broadcasting each chunk, so the live and backlog views stay in sync.
        // The first chunk a node emits is preceded by an `[Ild: <label>]`
        // transition marker (styled like the adapters' `[tool: ...]` markers)
        // so the live view makes the hand-off between nodes obvious.
        var headerEmitted = 0;
        var ctx = new NodeExecutionContext(run, node, sp, ct,
            async line =>
            {
                if (Interlocked.Exchange(ref headerEmitted, 1) == 0)
                {
                    var header = $"\n[Ild: {NodeDisplayName(node)}]\n";
                    var headerSeq = _progressBuffer.Append(run.Id, header);
                    await _notifier.NodeProgressAsync(run.Id, node.Id, header, headerSeq);
                }
                var seq = _progressBuffer.Append(run.Id, line);
                await _notifier.NodeProgressAsync(run.Id, node.Id, line, seq);
            });

        IAsyncEnumerator<NodeOutcome>? enumerator = null;
        try
        {
            enumerator = executor.ExecuteAsync(ctx).GetAsyncEnumerator(ct);
            while (true)
            {
                NodeOutcome outcome;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    outcome = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation (e.g. the run was stopped while a process-based
                    // node was executing) is not a node failure. Mark the in-flight
                    // node as interrupted and let the cancellation propagate up to
                    // LaunchAsync, which swallows it cleanly.
                    if (runNodeId is not null)
                    {
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Interrupted, null, "Run cancelled");
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Interrupted);
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = new NodeOutcome.Fail(EdgeType.OnFailure, ex.Message);
                }

                switch (outcome)
                {
                    case NodeOutcome.NodeStarting ns:
                    {
                        if (!await ReloadRunStillCurrentAsync(loopRunStore, run, entryStatus))
                            return ParkResult.Stop;
                        var rn = new LoopRunNode
                        {
                            Id = Guid.NewGuid(),
                            LoopRunId = run.Id,
                            LoopNodeId = node.Id,
                            NodeLabel = node.Label,
                            Status = LoopRunNodeStatus.Running,
                            StartedAt = DateTime.UtcNow,
                            EffectiveInput = ns.EffectiveInput,
                            PreviousNodeId = await FindPreviousRunNodeIdAsync(loopRunStore, run.Id),
                            IncomingEdgeId = run.IncomingEdgeId,
                        };
                        await loopRunStore.CreateRunNodeAsync(rn);
                        runNodeId = rn.Id;
                        run.CurrentNodeId = node.Id;
                        run.IncomingEdgeId = null;
                        run.NodeExecutionCount++;
                        await loopRunStore.UpdateRunAsync(run);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running);
                        // The work item's current step just changed; nudge the
                        // taskboard (which listens on the work-item hub, not the
                        // per-run hub) to refresh the card. Best-effort.
                        await TrySafe(() => _workItemNotifier.RunProgressedAsync(run.WorkItemId));
                        if (eventLog is not null)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "NodeStarted", ns.EffectiveInput ?? string.Empty, node.Id, runNodeId: rn.Id));
                        break;
                    }
                    case NodeOutcome.Success ok:
                    {
                        // Reload before persisting: the node may have run for
                        // hours and the user may have paused/cancelled/pinned
                        // the run meanwhile — a stale full-entity write would
                        // silently revert those. Record the node's result
                        // either way (the work happened), but stop without
                        // routing when the run state changed underneath us.
                        var stillCurrent = await ReloadRunStillCurrentAsync(loopRunStore, run, entryStatus);
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Succeeded, ok.Output, null, ok.Usage);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded);
                        if (eventLog is not null && runNodeId is Guid id)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "NodeCompleted", ok.Output ?? string.Empty, node.Id, runNodeId: id));
                        if (!stillCurrent)
                            return ParkResult.Stop;
                        run.PreviousNodeOutput = ok.Output;
                        run.ExternalActionResult = null;
                        run.ExternalActionResultType = ExternalActionResultType.Success;
                        run.ExternalActionEdgeName = null;
                        await loopRunStore.UpdateRunAsync(run);
                        // Surface each AI node's output as a conversation turn so the
                        // coder ↔ reviewer ↔ human dialogue can be followed in the UI.
                        // Authored by the node's title; best-effort so a conversation
                        // write never blocks or breaks the run.
                        if (node.NodeType == NodeType.AI && !string.IsNullOrWhiteSpace(ok.Output))
                            await TrySafe(() => workItems.AppendAiTurnAsync(run.WorkItemId, node.Label, ok.Output!));
                        var successEdge = await ResolveNextEdgeAsync(loopRunStore, node.Id, ok.Edge, ok.EdgeName);
                        if (successEdge is null)
                        {
                            // A named custom edge with no matching connection is a
                            // template wiring error, not a graph terminus: fail the
                            // node so the human can wire it. The default OnSuccess
                            // sink still completes the run.
                            if (ok.Edge == EdgeType.Custom && !string.IsNullOrEmpty(ok.EdgeName))
                            {
                                await FailRunAsync(run, $"missing edge connection: {ok.EdgeName}", loopRunStore, sp);
                                return ParkResult.Stop;
                            }
                            return await CompleteRunAsync(run, loopRunStore, workItems, ok.Output);
                        }
                        if (await TraversalLimitExceededAsync(run, successEdge, edgeTraversalCount, loopRunStore, sp))
                            return ParkResult.Stop;
                        run.CurrentNodeId = successEdge.TargetNodeId;
                        run.IncomingEdgeId = successEdge.Id;
                        await loopRunStore.UpdateRunAsync(run);
                        // Surface the edge taken so the run timeline/log shows the
                        // edge's name ("true"/"false", "Respond", a custom edge
                        // name) instead of only the node's Succeeded/Failed status.
                        // Best-effort; attributed to the node the edge left.
                        if (eventLog is not null && runNodeId is Guid successFromId)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "EdgeTraversed", EdgeDisplayName(successEdge), node.Id, runNodeId: successFromId));
                        return ParkResult.Continue;
                    }
                    case NodeOutcome.Fail f:
                    {
                        if (ct.IsCancellationRequested)
                        {
                            // The run was stopped while this node was running; the AI
                            // adapter killed the agent and surfaced this as a Fail
                            // (e.g. "claude-code timed out"). Record an interruption
                            // rather than a failure, and stop without traversing the
                            // OnFailure edge into another node.
                            await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Interrupted, f.Output, "Run cancelled");
                            if (runNodeId is not null)
                                await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Interrupted);
                            return ParkResult.Stop;
                        }
                        // Same stale-write guard as the Success branch.
                        var stillCurrent = await ReloadRunStillCurrentAsync(loopRunStore, run, entryStatus);
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Failed, f.Output, f.Reason);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Failed);
                        if (eventLog is not null && runNodeId is Guid id)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "NodeFailed", f.Reason, node.Id, runNodeId: id));
                        if (!stillCurrent)
                            return ParkResult.Stop;
                        run.PreviousNodeOutput = f.Output ?? f.Reason;
                        run.ExternalActionResult = null;
                        run.ExternalActionResultType = ExternalActionResultType.Success;
                        run.ExternalActionEdgeName = null;
                        await loopRunStore.UpdateRunAsync(run);
                        var failEdge = await ResolveNextEdgeAsync(loopRunStore, node.Id, f.Edge);
                        if (failEdge is null)
                        {
                            await FailRunAsync(run, f.Reason, loopRunStore, sp);
                            return ParkResult.Stop;
                        }
                        if (await TraversalLimitExceededAsync(run, failEdge, edgeTraversalCount, loopRunStore, sp))
                            return ParkResult.Stop;
                        run.CurrentNodeId = failEdge.TargetNodeId;
                        run.IncomingEdgeId = failEdge.Id;
                        await loopRunStore.UpdateRunAsync(run);
                        // Surface the edge taken on the failure route too (see the
                        // Success branch). Best-effort.
                        if (eventLog is not null && runNodeId is Guid failFromId)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "EdgeTraversed", EdgeDisplayName(failEdge), node.Id, runNodeId: failFromId));
                        return ParkResult.Continue;
                    }
                    case NodeOutcome.WaitingAction wa:
                    {
                        var stillCurrent = await ReloadRunStillCurrentAsync(loopRunStore, run, entryStatus);
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.WaitingHuman, wa.Output, null);
                        if (!stillCurrent)
                            return ParkResult.Stop;
                        var oldStatus = run.Status;
                        run.Status = LoopRunStatus.WaitingHuman;
                        run.HumanFeedbackReason = wa.Reason;
                        // Reset the PR heartbeat baseline so the poller treats a
                        // state already true at park time (e.g. CI already red, or
                        // a still-red state on a re-park after a fix loop) as a
                        // transition and fires on the first poll.
                        if (wa.Reason == HumanFeedbackReasons.PrAwaitingMerge)
                            run.PrPolledEdgeStates = null;
                        await loopRunStore.UpdateRunAsync(run);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.WaitingHuman);
                        await _notifier.RunStateChangedAsync(run.Id, oldStatus, LoopRunStatus.WaitingHuman);
                        var outEdges = await loopRunStore.GetEdgesForNodeIdsAsync(new[] { node.Id });
                        // Custom edges surface by their name (the Human node's button
                        // labels); default/fallback edges surface by their role name.
                        var actions = string.Join(",", outEdges
                            .Where(e => e.SourceNodeId == node.Id)
                            .Select(e => e.EdgeType == EdgeType.Custom ? e.Name : e.EdgeType.ToString())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Distinct());
                        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
                            reason: wa.Reason, actions: string.IsNullOrEmpty(actions) ? null : actions,
                            humanFeedbackReason: wa.Reason, currentLoopRunId: run.Id, name: node.Label);
                        return ParkResult.Stop;
                    }
                    case NodeOutcome.WaitingIld wi:
                    {
                        // Engine has not created a LoopRunNode yet (capacity gate fires before NodeStarting).
                        var transitioned = await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.WaitingForIld,
                            reason: wi.Reason, humanFeedbackReason: HumanFeedbackReasons.AiProviderThrottled);
                        if (!transitioned)
                            // The scheduler only auto-resumes items the server reports as
                            // WaitingForIld; with the transition lost this run stays parked
                            // until the next restart's reconciliation.
                            _logger.LogWarning(
                                "Failed to mark work item {WorkItemId} WaitingForIld for run {RunId}; run stays parked until restart",
                                run.WorkItemId, run.Id);
                        return ParkResult.Stop;
                    }
                    case NodeOutcome.Terminal t:
                    {
                        var stillCurrent = await ReloadRunStillCurrentAsync(loopRunStore, run, entryStatus);
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Succeeded, t.Output, null);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded);
                        if (!stillCurrent)
                            return ParkResult.Stop;
                        await CompleteRunAsync(run, loopRunStore, workItems, t.Output);
                        return ParkResult.Stop;
                    }
                    case NodeOutcome.WorktreeReady wr:
                    {
                        // Record the worktree/branch even if the run was cancelled
                        // mid-node — without these fields on the row the retention
                        // sweeper can never reclaim what was just created.
                        await TrySafe(() => loopRunStore.ReloadAsync(run));
                        run.WorktreePath = wr.WorktreePath;
                        run.BranchName = wr.BranchName;
                        await loopRunStore.UpdateRunAsync(run);
                        break;
                    }
                    case NodeOutcome.PrCreated pc:
                    {
                        await TrySafe(() => loopRunStore.ReloadAsync(run));
                        run.PrUrl = pc.PrUrl;
                        await loopRunStore.UpdateRunAsync(run);
                        break;
                    }
                    case NodeOutcome.WorktreeDestroyed _:
                    {
                        await TrySafe(() => loopRunStore.ReloadAsync(run));
                        run.WorktreePath = null;
                        run.BranchName = null;
                        await loopRunStore.UpdateRunAsync(run);
                        break;
                    }
                    case NodeOutcome.SessionBound sb:
                    {
                        try { await loopRunStore.UpsertSessionBindingAsync(run.Id, node.NodeType.ToString(), sb.SessionPlaceholder, sb.SessionId); } catch { }
                        break;
                    }
                }
            }
        }
        finally
        {
            if (enumerator is not null) await enumerator.DisposeAsync();
        }
        return ParkResult.Stop;
    }

    /// <summary>
    /// Refresh the engine's (possibly long-held) run instance with the row's
    /// current column values so persisting node results can't clobber
    /// concurrent control-plane writes (pause, cancel, retain-pin) made by
    /// other scopes while the node was executing. Returns false when the
    /// run's status changed underneath the engine — the caller must stop
    /// without routing or overwriting that state.
    /// </summary>
    private static async Task<bool> ReloadRunStillCurrentAsync(ILoopRunStore store, LoopRun run, LoopRunStatus entryStatus)
    {
        try { await store.ReloadAsync(run); }
        catch { return false; }
        return run.Status == entryStatus;
    }

    /// <summary>Human-readable name for a node's live-output transition marker,
    /// falling back to the node type when the template left the label blank.</summary>
    private static string NodeDisplayName(LoopNode node)
        => string.IsNullOrWhiteSpace(node.Label) ? node.NodeType.ToString() : node.Label;

    /// <summary>Human-readable label for a traversed edge: custom edges surface by
    /// their name (e.g. "Respond", "true"/"false"); default and fallback edges
    /// surface by their role. Mirrors how Human-node actions are labelled.</summary>
    private static string EdgeDisplayName(LoopNodeEdge edge)
        => edge.EdgeType == EdgeType.Custom && !string.IsNullOrEmpty(edge.Name)
            ? edge.Name!
            : edge.EdgeType.ToString();

    private static async Task<Guid?> FindPreviousRunNodeIdAsync(ILoopRunStore store, Guid runId)
    {
        var nodes = await store.GetRunNodesAsync(runId);
        return nodes
            .Where(n => n.CompletedAt.HasValue)
            .OrderByDescending(n => n.CompletedAt)
            .FirstOrDefault()?.Id;
    }

    private static async Task CompleteRunNodeAsync(ILoopRunStore store, Guid? runNodeId, LoopRunNodeStatus status, string? output, string? error, ILD.Data.DTOs.TokenUsage? usage = null)
    {
        if (runNodeId is null) return;
        var rn = await store.GetRunNodeByIdAsync(runNodeId.Value);
        if (rn is null) return;
        rn.Status = status;
        rn.Output = output;
        rn.Error = error;
        rn.CompletedAt = DateTime.UtcNow;
        if (usage is not null)
        {
            // Stamp the provider even when no tokens were reported so the run
            // can still be attributed to a provider on the dashboard.
            rn.AiProvider = usage.AiProvider;
            if (usage.HasData)
            {
                rn.InputTokens = usage.InputTokens;
                rn.OutputTokens = usage.OutputTokens;
                rn.CostUsd = usage.CostUsd;
            }
        }
        await store.UpdateRunNodeAsync(rn);
    }

    private static async Task<LoopNodeEdge?> ResolveNextEdgeAsync(ILoopRunStore runStore, Guid fromNodeId, EdgeType edge, string? name = null)
    {
        var edges = await runStore.GetEdgesForNodeIdsAsync(new[] { fromNodeId });
        return edges.FirstOrDefault(e => e.SourceNodeId == fromNodeId && e.EdgeType == edge && e.Name == name);
    }

    private static async Task<Dictionary<Guid, int>> RebuildEdgeTraversalCountsAsync(ILoopRunStore store, Guid runId)
    {
        var counts = new Dictionary<Guid, int>();
        var nodes = await store.GetRunNodesAsync(runId);
        foreach (var rn in nodes)
        {
            if (rn.IncomingEdgeId is Guid eid)
                counts[eid] = counts.GetValueOrDefault(eid) + 1;
        }
        return counts;
    }

    private async Task<bool> TraversalLimitExceededAsync(
        LoopRun run, LoopNodeEdge edge, Dictionary<Guid, int> counts, ILoopRunStore store, IServiceProvider sp)
    {
        counts[edge.Id] = counts.GetValueOrDefault(edge.Id) + 1;
        var limit = edge.MaxTraversals ?? DefaultMaxEdgeTraversals;
        if (counts[edge.Id] > limit)
        {
            await FailRunAsync(run, $"Max traversals exceeded for edge {edge.Id} (limit {limit})", store, sp);
            return true;
        }
        return false;
    }

    private async Task<ParkResult> CompleteRunAsync(LoopRun run, ILoopRunStore store, IWorkItemManager workItems, string? output)
    {
        var old = run.Status;
        run.Status = LoopRunStatus.Completed;
        run.CompletedAt = DateTime.UtcNow;
        await store.UpdateRunAsync(run);
        _progressBuffer.Clear(run.Id);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.Done, currentLoopRunId: run.Id);
        return ParkResult.Stop;
    }

    private async Task FailRunAsync(LoopRun run, string reason, ILoopRunStore store, IServiceProvider sp)
    {
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        run.Status = LoopRunStatus.Failed;
        run.CompletedAt = DateTime.UtcNow;
        run.HumanFeedbackReason = reason;
        await store.UpdateRunAsync(run);
        _progressBuffer.Clear(run.Id);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            reason: reason, humanFeedbackReason: HumanFeedbackReasons.NodeFailed, currentLoopRunId: run.Id);
    }

    /// <summary>
    /// Terminal handler for an unhandled exception in the run loop. Marks the
    /// run Failed and parks the work item for review, but only if the run is
    /// still Running — a crash during a transition that already moved the run
    /// to a terminal/parked state must not clobber that state.
    /// </summary>
    private async Task MarkRunCrashedAsync(Guid runId, string reason)
    {
        using var scope = _sp.CreateScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<ILoopRunStore>();
        var run = await store.GetByIdAsync(runId);
        if (run is null || run.Status != LoopRunStatus.Running) return;

        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var old = run.Status;
        run.Status = LoopRunStatus.Failed;
        run.CompletedAt = DateTime.UtcNow;
        run.HumanFeedbackReason = reason;
        await store.UpdateRunAsync(run);
        _progressBuffer.Clear(runId);
        await _notifier.RunStateChangedAsync(runId, old, LoopRunStatus.Failed);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            reason: reason, humanFeedbackReason: HumanFeedbackReasons.RunCrashed, currentLoopRunId: run.Id);
    }

    private static async Task TrySafe(Func<Task> f) { try { await f(); } catch { } }
}
