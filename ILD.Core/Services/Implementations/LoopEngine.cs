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
        IWorkItemNotifier? workItemNotifier = null)
    {
        _sp = sp;
        _registry = registry;
        _notifier = notifier;
        _logger = logger;
        _workItemNotifier = workItemNotifier ?? new NoopWorkItemNotifier();
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
        var version = await templateStore.GetLatestVersionAsync(resolution.TemplateId.Value)
            ?? throw new InvalidOperationException("Template has no version");
        var startNode = await loopRunStore.GetStartNodeAsync(version.Id)
            ?? throw new InvalidOperationException("Template has no Start node");

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            CurrentNodeId = startNode.Id,
            RepositoryId = wi.RepositoryId,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        await loopRunStore.CreateRunAsync(run);
        await workItems.TransitionAsync(workItemId, RemoteWorkItemStatus.Running, currentLoopRunId: run.Id);
        await _notifier.RunStateChangedAsync(run.Id, LoopRunStatus.Running, LoopRunStatus.Running);

        _ = LaunchAsync(run.Id);
    }

    public async Task ResumeRecoveredRunAsync(Guid runId)
    {
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
        if (_runCts.TryGetValue(runId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        await _notifier.RunStateChangedAsync(runId, old, LoopRunStatus.Cancelled);
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            reason: HumanFeedbackReasons.RunCancelled, humanFeedbackReason: HumanFeedbackReasons.RunCancelled);
    }

    public async Task<LoopRunStatus?> GetRunStatusAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<ILoopRunStore>().GetByIdAsync(runId);
        return run?.Status;
    }

    public Task<IEnumerable<Guid>> GetActiveRunIdsAsync() =>
        Task.FromResult<IEnumerable<Guid>>(_runTasks.Keys.ToArray());

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
        run.CurrentNodeId = target.LoopNodeId;
        run.PreviousNodeOutput = prevOutput;
        run.ExternalActionResult = null;
        run.ExternalActionResultType = ExternalActionResultType.Success;
        run.Status = LoopRunStatus.Running;
        run.IsPaused = false;
        await loopRunStore.UpdateRunAsync(run);
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
        // Idempotency: if a background loop is already driving this run we
        // must not start a second one. Two concurrent loops on the same run
        // produce duplicated LoopRunNode rows and corrupt visit counts.
        if (_runTasks.TryGetValue(runId, out var inflight) && !inflight.IsCompleted)
        {
            _logger.LogDebug("LaunchAsync skipped for run {RunId}: already active", runId);
            return Task.CompletedTask;
        }
        var cts = new CancellationTokenSource();
        if (!_runCts.TryAdd(runId, cts))
        {
            cts.Dispose();
            return Task.CompletedTask;
        }
        var task = Task.Run(async () =>
        {
            try { await RunUntilParkAsync(runId, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "Run {RunId} crashed", runId); }
            finally
            {
                _runCts.TryRemove(runId, out _);
                _runTasks.TryRemove(runId, out _);
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
        var edgeTraversalCount = new Dictionary<Guid, int>();

        while (!ct.IsCancellationRequested)
        {
            var run = await loopRunStore.GetByIdAsync(runId);
            if (run is null || run.Status != LoopRunStatus.Running || run.IsPaused) return;
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

        var ctx = new NodeExecutionContext(run, node, sp, ct,
            line => _notifier.NodeProgressAsync(run.Id, node.Id, line));

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
                catch (Exception ex)
                {
                    outcome = new NodeOutcome.Fail(EdgeType.OnFailure, ex.Message);
                }

                switch (outcome)
                {
                    case NodeOutcome.NodeStarting ns:
                    {
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
                        };
                        await loopRunStore.CreateRunNodeAsync(rn);
                        runNodeId = rn.Id;
                        run.CurrentNodeId = node.Id;
                        run.NodeExecutionCount++;
                        await loopRunStore.UpdateRunAsync(run);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running);
                        if (eventLog is not null)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "NodeStarted", ns.EffectiveInput ?? string.Empty, node.Id, runNodeId: rn.Id));
                        break;
                    }
                    case NodeOutcome.Success ok:
                    {
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Succeeded, ok.Output, null);
                        run.PreviousNodeOutput = ok.Output;
                        run.ExternalActionResult = null;
                        run.ExternalActionResultType = ExternalActionResultType.Success;
                        await loopRunStore.UpdateRunAsync(run);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded);
                        if (eventLog is not null && runNodeId is Guid id)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "NodeSucceeded", ok.Output ?? string.Empty, node.Id, runNodeId: id));
                        var successEdge = await ResolveNextEdgeAsync(loopRunStore, node.Id, ok.Edge);
                        if (successEdge is null) return await CompleteRunAsync(run, loopRunStore, workItems, ok.Output);
                        if (await TraversalLimitExceededAsync(run, successEdge, edgeTraversalCount, loopRunStore, sp))
                            return ParkResult.Stop;
                        run.CurrentNodeId = successEdge.TargetNodeId;
                        await loopRunStore.UpdateRunAsync(run);
                        return ParkResult.Continue;
                    }
                    case NodeOutcome.Fail f:
                    {
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Failed, f.Output, f.Reason);
                        run.PreviousNodeOutput = f.Output ?? f.Reason;
                        run.ExternalActionResult = null;
                        run.ExternalActionResultType = ExternalActionResultType.Success;
                        await loopRunStore.UpdateRunAsync(run);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Failed);
                        if (eventLog is not null && runNodeId is Guid id)
                            await TrySafe(() => eventLog.AppendAsync(run.Id, "NodeFailed", f.Reason, node.Id, runNodeId: id));
                        var failEdge = await ResolveNextEdgeAsync(loopRunStore, node.Id, f.Edge);
                        if (failEdge is null)
                        {
                            await FailRunAsync(run, f.Reason, loopRunStore, sp);
                            return ParkResult.Stop;
                        }
                        if (await TraversalLimitExceededAsync(run, failEdge, edgeTraversalCount, loopRunStore, sp))
                            return ParkResult.Stop;
                        run.CurrentNodeId = failEdge.TargetNodeId;
                        await loopRunStore.UpdateRunAsync(run);
                        return ParkResult.Continue;
                    }
                    case NodeOutcome.WaitingAction wa:
                    {
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.WaitingHuman, wa.Output, null);
                        var oldStatus = run.Status;
                        run.Status = LoopRunStatus.WaitingHuman;
                        run.HumanFeedbackReason = wa.Reason;
                        await loopRunStore.UpdateRunAsync(run);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.WaitingHuman);
                        await _notifier.RunStateChangedAsync(run.Id, oldStatus, LoopRunStatus.WaitingHuman);
                        var outEdges = await loopRunStore.GetEdgesForNodeIdsAsync(new[] { node.Id });
                        var actions = string.Join(",", outEdges
                            .Where(e => e.SourceNodeId == node.Id)
                            .Select(e => e.EdgeType.ToString())
                            .Distinct());
                        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
                            reason: wa.Reason, actions: string.IsNullOrEmpty(actions) ? null : actions,
                            humanFeedbackReason: wa.Reason, currentLoopRunId: run.Id);
                        return ParkResult.Stop;
                    }
                    case NodeOutcome.WaitingIld wi:
                    {
                        // Engine has not created a LoopRunNode yet (capacity gate fires before NodeStarting).
                        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.WaitingForIld,
                            reason: wi.Reason, humanFeedbackReason: HumanFeedbackReasons.AiProviderThrottled);
                        return ParkResult.Stop;
                    }
                    case NodeOutcome.Terminal t:
                    {
                        await CompleteRunNodeAsync(loopRunStore, runNodeId, LoopRunNodeStatus.Succeeded, t.Output, null);
                        await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded);
                        await CompleteRunAsync(run, loopRunStore, workItems, t.Output);
                        return ParkResult.Stop;
                    }
                    case NodeOutcome.WorktreeReady wr:
                    {
                        run.WorktreePath = wr.WorktreePath;
                        run.BranchName = wr.BranchName;
                        await loopRunStore.UpdateRunAsync(run);
                        break;
                    }
                    case NodeOutcome.PrCreated pc:
                    {
                        run.PrUrl = pc.PrUrl;
                        await loopRunStore.UpdateRunAsync(run);
                        break;
                    }
                    case NodeOutcome.WorktreeDestroyed _:
                    {
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

    private static async Task<Guid?> FindPreviousRunNodeIdAsync(ILoopRunStore store, Guid runId)
    {
        var nodes = await store.GetRunNodesAsync(runId);
        return nodes
            .Where(n => n.CompletedAt.HasValue)
            .OrderByDescending(n => n.CompletedAt)
            .FirstOrDefault()?.Id;
    }

    private static async Task CompleteRunNodeAsync(ILoopRunStore store, Guid? runNodeId, LoopRunNodeStatus status, string? output, string? error)
    {
        if (runNodeId is null) return;
        var rn = await store.GetRunNodeByIdAsync(runNodeId.Value);
        if (rn is null) return;
        rn.Status = status;
        rn.Output = output;
        rn.Error = error;
        rn.CompletedAt = DateTime.UtcNow;
        await store.UpdateRunNodeAsync(rn);
    }

    private static async Task<LoopNodeEdge?> ResolveNextEdgeAsync(ILoopRunStore runStore, Guid fromNodeId, EdgeType edge)
    {
        var edges = await runStore.GetEdgesForNodeIdsAsync(new[] { fromNodeId });
        return edges.FirstOrDefault(e => e.SourceNodeId == fromNodeId && e.EdgeType == edge);
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

    private static async Task<ParkResult> CompleteRunAsync(LoopRun run, ILoopRunStore store, IWorkItemManager workItems, string? output)
    {
        var old = run.Status;
        run.Status = LoopRunStatus.Completed;
        run.CompletedAt = DateTime.UtcNow;
        await store.UpdateRunAsync(run);
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
        await workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            reason: reason, humanFeedbackReason: HumanFeedbackReasons.NodeFailed, currentLoopRunId: run.Id);
    }

    private static async Task TrySafe(Func<Task> f) { try { await f(); } catch { } }
}
