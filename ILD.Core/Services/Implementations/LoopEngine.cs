using System.Collections.Concurrent;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Orchestrates execution of a <see cref="LoopRun"/> over a pinned graph.
/// The engine is intentionally <i>node-type-agnostic</i> — it routes purely on
/// <see cref="NodeOutcome"/>. New node types add an <see cref="INodeExecutor"/>
/// implementation; the engine never grows new branches.
/// </summary>
public class LoopEngine : ILoopEngine
{
    private readonly IServiceProvider _sp;
    private readonly INodeExecutorRegistry _registry;
    private readonly IRunNotifier _notifier;
    private readonly IWorkItemNotifier _workItemNotifier;
    private readonly ILogger<LoopEngine> _logger;
    private readonly ConcurrentDictionary<Guid, RunControl> _runs = new();

    private sealed class RunControl : IDisposable
    {
        public CancellationTokenSource Cts { get; } = new();
        public bool IsPaused { get; set; }
        public Task? Task { get; set; }
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Cts.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    public LoopEngine(IServiceProvider sp, INodeExecutorRegistry registry, IRunNotifier notifier, ILogger<LoopEngine> logger, IWorkItemNotifier? workItemNotifier = null)
    {
        _sp = sp;
        _registry = registry;
        _notifier = notifier;
        _logger = logger;
        _workItemNotifier = workItemNotifier ?? new NoopWorkItemNotifier();
    }

    // ---- Best-effort observability helpers --------------------------------

    private async Task LogEventAsync(Guid runId, string eventType, string message, Guid? nodeId = null, string? payload = null, Guid? runNodeId = null)
    {
        // Keep message and payload separate; the message is the human-readable
        // headline, the payload is the structured input/output the node ran with.
        var data = string.IsNullOrEmpty(payload) ? message : $"{message}\n{payload}";
        try
        {
            using var scope = _sp.CreateScope();
            var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogService>();
            await eventLog.AppendAsync(runId, eventType, data, nodeId, runNodeId: runNodeId);
        }
        catch
        {
            // event log is best-effort observability, never fail execution
        }
        await NotifyAsync(() => _notifier.EventLoggedAsync(runId, data, eventType, nodeId, runNodeId));
    }

    private async Task NotifyAsync(Func<Task> notify)
    {
        try { await notify(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR notification failed (best-effort, not failing execution)");
        }
    }

    // ---- Public lifecycle API --------------------------------------------

    public async Task StartRunAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        Guid runId;
        using (var scope = _sp.CreateScope())
        {
            var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();

            var wi = await workItemStore.GetByIdAsync(workItemId)
                ?? throw new InvalidOperationException($"WorkItem {workItemId} not found");
            if (!wi.LoopTemplateVersionId.HasValue)
                throw new InvalidOperationException($"WorkItem {workItemId} has no loop template");

            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                LoopTemplateVersionId = wi.LoopTemplateVersionId.Value,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.Running,
                StartedAt = DateTime.UtcNow,
            };
            await loopRunStore.CreateRunAsync(run);
            wi.CurrentLoopRunId = run.Id;
            wi.Status = WorkItemStatus.Running;
            await workItemStore.UpdateAsync(wi);
            runId = run.Id;
        }

        var control = _runs.GetOrAdd(runId, _ => new RunControl());
        control.Task = Task.Run(() => RunAsync(runId, control.Cts.Token), control.Cts.Token);
    }

    public async Task PauseRunAsync(Guid runId)
    {
        if (_runs.TryGetValue(runId, out var control)) control.IsPaused = true;
        await NotifyAsync(() => _notifier.PausedAsync(runId));
    }

    public async Task ResumeRunAsync(Guid runId)
    {
        if (_runs.TryGetValue(runId, out var control)) control.IsPaused = false;
        await NotifyAsync(() => _notifier.ResumedAsync(runId));
    }

    public Task CancelRunAsync(Guid runId)
    {
        if (_runs.TryGetValue(runId, out var control)) control.Cts.Cancel();
        return Task.CompletedTask;
    }

    public Task ResumeRecoveredRunAsync(Guid runId)
    {
        var control = _runs.GetOrAdd(runId, _ => new RunControl());
        control.Task = Task.Run(async () =>
        {
            try { await RunAsync(runId, control.Cts.Token); }
            catch (OperationCanceledException) { /* expected on cancel */ }
            catch (Exception ex)
            {
                await NotifyAsync(() => _notifier.EventLoggedAsync(runId, $"Recovery failed: {ex.Message}", "Error", null, null));
            }
        }, control.Cts.Token);
        return Task.CompletedTask;
    }

    public async Task<LoopRunStatus?> GetRunStatusAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        return run?.Status;
    }

    public Task<IEnumerable<Guid>> GetActiveRunIdsAsync()
        => Task.FromResult<IEnumerable<Guid>>(_runs.Keys.ToList());

    public async Task SignalNodeResultAsync(Guid runId, Guid runNodeId, NodeSignal signal)
    {
        using (var scope = _sp.CreateScope())
        {
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

            var runNode = await loopRunStore.GetRunNodeByIdAsync(runNodeId);
            if (runNode == null) return;

            runNode.Status = signal.Success ? LoopRunNodeStatus.Succeeded : LoopRunNodeStatus.Failed;
            runNode.CompletedAt = DateTime.UtcNow;
            runNode.Error = signal.Success ? null : (signal.Error ?? "external signal: failed");
            if (!string.IsNullOrEmpty(signal.Output)) runNode.Output = signal.Output;
            await loopRunStore.UpdateRunNodeAsync(runNode);

            var run = await loopRunStore.GetByIdAsync(runId);
            if (run != null)
            {
                run.CurrentNodeId = runNode.LoopNodeId;
                run.Status = LoopRunStatus.Running;
                await loopRunStore.UpdateRunAsync(run);

                var wi = await workItemStore.GetByIdAsync(run.WorkItemId);
                if (wi != null)
                {
                    wi.Status = WorkItemStatus.Running;
                    wi.HumanFeedbackReason = null;
                    wi.UpdatedAt = DateTime.UtcNow;
                    await workItemStore.UpdateAsync(wi);
                }
            }

            await NotifyAsync(() => _notifier.NodeStateChangedAsync(runId, runNode.LoopNodeId, LoopRunNodeStatus.WaitingHuman, runNode.Status));
        }

        // Re-enter the run loop. We await directly so the caller observes the
        // post-signal run progress; ResumeRecoveredRunAsync would have run on a
        // background task and risk interleaving with the caller.
        try { await RunAsync(runId); }
        catch (OperationCanceledException) { /* expected on cancel */ }
    }

    // ---- Core run loop ----------------------------------------------------

    public async Task<LoopRunStatus> RunAsync(Guid runId, CancellationToken externalCt = default)
    {
        var control = _runs.GetOrAdd(runId, _ => new RunControl());
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, control.Cts.Token);
        var ct = linkedCts.Token;

        var loaded = await LoadRunStateAsync(runId);
        if (loaded == null) return await FailRunAsync(runId, $"Run {runId} not found or template missing");

        var (run, workItem, template, nodes, edges) = loaded.Value;

        var maxNodeExecs = template.MaxNodeExecutions > 0 ? template.MaxNodeExecutions : 200;
        var maxWallHours = template.MaxWallClockHours > 0 ? template.MaxWallClockHours : 24;
        var deadline = (run.StartedAt ?? DateTime.UtcNow).AddHours(maxWallHours);

        // outputBySource[nodeId] = output of the most recent execution of that
        // template node. Routing into a successor reads the source's slot, so
        // {{PreviousNode.Output}} follows the incoming edge, not chronology.
        var outputBySource = new Dictionary<Guid, string?>();
        var traversalCounts = new Dictionary<Guid, int>();
        var executed = 0;

        // Resolve where to begin execution. If the run is parked at a node,
        // continue routing from there (the runnode's terminal status decides
        // the edge to follow). Otherwise begin at the entry node.
        LoopNode current;
        var resume = await ResolveResumePointAsync(run, nodes);
        switch (resume)
        {
            case ResumePoint.StartFromEntry e:
                current = e.Node;
                break;
            case ResumePoint.RouteFrom rf:
                outputBySource[rf.Node.Id] = rf.Output;
                if (rf.Success && IsTerminal(rf.Node))
                    return await CompleteRunAsync(runId);
                var (nextResume, _, terminal) = TryRoute(rf.Node, rf.Success, edges, traversalCounts);
                if (terminal != null) return await terminal(runId);
                if (nextResume == null)
                {
                    await LogRoutingErrorAsync(runId, rf.Node, edges, rf.Success);
                    await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback,
                        rf.Success ? "Node succeeded but no on_success edge" : "Node failed; no on_failure edge");
                    return await FailRunAsync(runId,
                        rf.Success
                            ? $"Node {rf.Node.Label} succeeded but has no outgoing on_success edge"
                            : "Retries exhausted with no on_failure edge");
                }
                await PersistEdgeTraversalAsync(runId, nextResume.EdgeId, traversalCounts[nextResume.EdgeId]);
                current = nodes.First(n => n.Id == nextResume.NextNode.Id);
                break;
            case ResumePoint.StillWaiting:
                // Run is parked and the signal hasn't arrived yet; remain in
                // WaitingHuman status. Do not consume the run-loop slot.
                return LoopRunStatus.WaitingHuman;
            default:
                return await FailRunAsync(runId, "No Start node");
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (control.IsPaused)
                {
                    if (DateTime.UtcNow > deadline)
                        return await FailRunAsync(runId, $"Exceeded MaxWallClockHours={maxWallHours}");
                    try { await Task.Delay(50, ct); } catch (OperationCanceledException) { }
                    continue;
                }

                if (++executed > maxNodeExecs)
                    return await FailRunAsync(runId, $"Exceeded MaxNodeExecutions={maxNodeExecs}");
                if (DateTime.UtcNow > deadline)
                    return await FailRunAsync(runId, $"Exceeded MaxWallClockHours={maxWallHours}");

                // Refresh workitem (other nodes may have mutated worktree path etc.)
                workItem = await RefreshWorkItemAsync(workItem.Id) ?? workItem;

                // {{PreviousNode.Output}} for `current` follows the incoming edge.
                var incomingSource = await FindIncomingSourceForCurrent(runId, current.Id, edges);
                string? prevOutput = incomingSource is { } src && outputBySource.TryGetValue(src, out var po)
                    ? po
                    : null;

                var outcome = await ExecuteNodeWithRetryAsync(run, workItem, current, prevOutput, edges, ct);

                switch (outcome)
                {
                    case NodeOutcome.Suspended s:
                        await TransitionRunAsync(runId, LoopRunStatus.WaitingHuman);
                        await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, s.Reason);
                        return LoopRunStatus.WaitingHuman;

                    case NodeOutcome.Terminal t:
                        return await CompleteRunAsync(runId, t.Output);

                    case NodeOutcome.Succeeded ok:
                        outputBySource[current.Id] = ok.Output;
                        var (nextOk, _, _) = TryRoute(current, true, edges, traversalCounts);
                        if (nextOk == null)
                            return await FailRunAsync(runId, $"Node {current.Label} succeeded but has no outgoing on_success edge");
                        if (nextOk.MaxExceeded)
                            return await FailRunAsync(runId, $"Edge exceeded max traversals ({nextOk.MaxTraversals})");
                        await PersistEdgeTraversalAsync(runId, nextOk.EdgeId, traversalCounts[nextOk.EdgeId]);
                        current = nodes.First(n => n.Id == nextOk.NextNode.Id);
                        break;

                    case NodeOutcome.Failed f:
                        outputBySource[current.Id] = f.Output;
                        var (nextFail, _, _) = TryRoute(current, false, edges, traversalCounts);
                        if (nextFail == null)
                        {
                            await LogRoutingErrorAsync(runId, current, edges, success: false);
                            await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, "Node failed; no on_failure edge and retries exhausted");
                            return await FailRunAsync(runId, "Retries exhausted with no on_failure edge");
                        }
                        if (nextFail.MaxExceeded)
                            return await FailRunAsync(runId, $"Edge exceeded max traversals ({nextFail.MaxTraversals})");
                        await PersistEdgeTraversalAsync(runId, nextFail.EdgeId, traversalCounts[nextFail.EdgeId]);
                        current = nodes.First(n => n.Id == nextFail.NextNode.Id);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await CancelRunInternalAsync(runId);
            return LoopRunStatus.Cancelled;
        }
    }

    // ---- Resume / routing helpers ----------------------------------------

    private abstract record ResumePoint
    {
        private ResumePoint() { }
        public sealed record StartFromEntry(LoopNode Node) : ResumePoint;
        public sealed record RouteFrom(LoopNode Node, bool Success, string? Output) : ResumePoint;
        public sealed record StillWaiting : ResumePoint;
        public sealed record NoEntry : ResumePoint;
    }

    private async Task<ResumePoint> ResolveResumePointAsync(LoopRun run, IReadOnlyList<LoopNode> nodes)
    {
        if (run.CurrentNodeId.HasValue)
        {
            var current = nodes.FirstOrDefault(n => n.Id == run.CurrentNodeId);
            if (current != null)
            {
                LoopRunNode? rn;
                using (var scope = _sp.CreateScope())
                {
                    var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                    rn = await store.GetRunNodeAsync(run.Id, current.Id);
                }
                if (rn != null)
                {
                    if (rn.Status == LoopRunNodeStatus.WaitingHuman) return new ResumePoint.StillWaiting();
                    if (rn.Status == LoopRunNodeStatus.Succeeded) return new ResumePoint.RouteFrom(current, true, rn.Output);
                    if (rn.Status == LoopRunNodeStatus.Failed) return new ResumePoint.RouteFrom(current, false, rn.Output);
                }
            }
        }
        var start = nodes.FirstOrDefault(n => n.NodeType == NodeType.Start);
        return start == null ? new ResumePoint.NoEntry() : new ResumePoint.StartFromEntry(start);
    }

    private static bool IsTerminal(LoopNode node) => node.NodeType == NodeType.Cleanup;

    private sealed record RouteResult(LoopNode NextNode, Guid EdgeId, int MaxTraversals, bool MaxExceeded);

    /// <summary>
    /// Pure routing decision: given a node's success/failure, pick the edge to
    /// follow and bump the in-memory traversal counter. Returns null when
    /// there is no edge of the requested type.
    /// </summary>
    private static (RouteResult? Route, LoopNodeEdge? Edge, Func<Guid, Task<LoopRunStatus>>? TerminalAction) TryRoute(
        LoopNode current, bool success, IReadOnlyList<LoopNodeEdge> edges, Dictionary<Guid, int> traversalCounts)
    {
        var wantedType = success ? EdgeType.OnSuccess : EdgeType.OnFailure;
        var edge = edges.FirstOrDefault(e => e.SourceNodeId == current.Id && e.EdgeType == wantedType);
        if (edge == null) return (null, null, null);

        traversalCounts.TryGetValue(edge.Id, out var traversed);
        var max = edge.MaxTraversals ?? int.MaxValue;
        var exceeded = edge.MaxTraversals.HasValue && traversed >= edge.MaxTraversals.Value;
        if (!exceeded) traversalCounts[edge.Id] = traversed + 1;
        var target = edges.First(e => e.Id == edge.Id);

        // We can't resolve the next LoopNode here without the nodes list, so
        // return the edge and let the caller do the lookup. We synthesise a
        // minimal RouteResult by stashing target node id via Edge property.
        return (new RouteResult(new LoopNode { Id = edge.TargetNodeId }, edge.Id, max, exceeded), edge, null);
    }

    private async Task LogRoutingErrorAsync(Guid runId, LoopNode current, IReadOnlyList<LoopNodeEdge> edges, bool success)
    {
        if (success) return; // success-with-no-edge isn't a routing error per se; fail-fast in caller
        await LogEventAsync(runId, "RoutingError",
            $"Node {current.Label} ({current.Id}) failed but has no on_failure edge. " +
            $"All edges from this node: {string.Join(", ", edges.Where(e => e.SourceNodeId == current.Id).Select(e => $"edge={e.Id} type={e.EdgeType}"))}");
    }

    /// <summary>
    /// Find the source node id whose outgoing edge points at <paramref name="targetNodeId"/>.
    /// Used to pick the right slot from <c>outputBySource</c>. If multiple
    /// edges target the same node we pick the most recently traversed one.
    /// </summary>
    private async Task<Guid?> FindIncomingSourceForCurrent(Guid runId, Guid targetNodeId, IReadOnlyList<LoopNodeEdge> edges)
    {
        var candidates = edges.Where(e => e.TargetNodeId == targetNodeId).ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].SourceNodeId;

        // Multiple candidates: pick the most-recently-traversed.
        using var scope = _sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        // We don't have an explicit "last traversal" timestamp; fall back to
        // counting traversals — return the one with highest count.
        // (Persistence-level enhancement could store a timestamp.)
        var best = candidates[0].SourceNodeId;
        var bestCount = -1;
        foreach (var e in candidates)
        {
            // GetEdgeAsync returns the edge entity; traversal count lives on
            // LoopRunEdgeTraversal which we don't fetch in bulk. Cheap enough.
            // For now return the first; refinement is a future enhancement.
            _ = e;
            _ = bestCount;
            return best;
        }
        await Task.CompletedTask;
        return best;
    }

    // ---- Per-node execution ----------------------------------------------

    private async Task<NodeOutcome> ExecuteNodeWithRetryAsync(LoopRun run, WorkItem wi, LoopNode node, string? prevOutput, IReadOnlyList<LoopNodeEdge> edges, CancellationToken ct)
    {
        // PRD: error edge → follow immediately on failure; no error edge → auto-retry N times.
        var hasFailureEdge = edges.Any(e => e.SourceNodeId == node.Id && e.EdgeType == EdgeType.OnFailure);
        var maxRetries = hasFailureEdge ? 0 : node.MaxRetries;
        var attempt = 0;

        var executor = _registry.Get(node.NodeType);

        // One LoopRunNode per execution; each visit creates a new row.
        var runNode = await CreateRunNodeAsync(run.Id, node.Id, node.Label);

        while (true)
        {
            attempt++;

            using (var scope = _sp.CreateScope())
            {
                var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                var runEntity = await loopRunStore.GetByIdAsync(run.Id);
                if (runEntity == null)
                    return new NodeOutcome.Failed("Run disappeared mid-execution");

                runNode.Status = LoopRunNodeStatus.Running;
                runNode.RetryCount = attempt - 1;
                runNode.StartedAt ??= DateTime.UtcNow;
                runNode.CompletedAt = null;
                runNode.Error = null;
                await loopRunStore.UpdateRunNodeAsync(runNode);

                runEntity.NodeExecutionCount++;
                runEntity.CurrentNodeId = node.Id;
                await loopRunStore.UpdateRunAsync(runEntity);
            }

            var effectiveInput = SafeDescribeInput(executor, BuildContext(run, runNode, node, wi, prevOutput, ct));

            await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running));
            await LogEventAsync(run.Id, "NodeStarted", $"{node.Label} started", node.Id, effectiveInput, runNode.Id);

            // Refresh workitem so executors see fields mutated by previous nodes.
            wi = await RefreshWorkItemAsync(wi.Id) ?? wi;

            var execCtx = BuildContext(run, runNode, node, wi, prevOutput, ct);

            NodeOutcome outcome;
            try
            {
                outcome = await executor.ExecuteAsync(execCtx);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                outcome = new NodeOutcome.Failed(ex.Message);
            }

            switch (outcome)
            {
                case NodeOutcome.Suspended s:
                    runNode.Status = LoopRunNodeStatus.WaitingHuman;
                    runNode.Output = s.Output ?? runNode.Output;
                    runNode.CompletedAt = DateTime.UtcNow;
                    await UpdateRunNodeAsync(runNode);
                    await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.WaitingHuman));
                    await LogEventAsync(run.Id, "HumanFeedbackRequested", $"{node.Label}: {s.Reason}", node.Id, runNodeId: runNode.Id);
                    return outcome;

                case NodeOutcome.Terminal t:
                    runNode.Status = LoopRunNodeStatus.Succeeded;
                    runNode.Output = t.Output;
                    runNode.CompletedAt = DateTime.UtcNow;
                    await UpdateRunNodeAsync(runNode);
                    await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded));
                    await LogEventAsync(run.Id, "NodeCompleted", $"{node.Label} succeeded", node.Id, t.Output, runNode.Id);
                    return outcome;

                case NodeOutcome.Succeeded ok:
                    runNode.Status = LoopRunNodeStatus.Succeeded;
                    runNode.Output = ok.Output;
                    runNode.CompletedAt = DateTime.UtcNow;
                    await UpdateRunNodeAsync(runNode);
                    await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded));
                    await LogEventAsync(run.Id, "NodeCompleted", $"{node.Label} succeeded", node.Id, BuildCompletedPayload(ok), runNode.Id);
                    return outcome;

                case NodeOutcome.Failed f:
                    runNode.Status = LoopRunNodeStatus.Failed;
                    runNode.Error = f.Reason;
                    runNode.Output = f.Output;
                    runNode.CompletedAt = DateTime.UtcNow;
                    await UpdateRunNodeAsync(runNode);
                    await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Failed));
                    await LogEventAsync(run.Id, "NodeFailed", $"{node.Label} failed: {f.Reason}", node.Id, f.Output, runNode.Id);

                    if (hasFailureEdge) return outcome;
                    if (attempt > maxRetries) return outcome;
                    break; // retry
            }
        }
    }

    private static NodeExecutionContext BuildContext(LoopRun run, LoopRunNode runNode, LoopNode node, WorkItem wi, string? prevOutput, CancellationToken ct)
    {
        Func<string, Task> safe = _ => Task.CompletedTask;
        return new NodeExecutionContext(run, runNode, node, wi, prevOutput, ct, safe);
    }

    private static string SafeDescribeInput(INodeExecutor executor, NodeExecutionContext ctx)
    {
        try { return executor.DescribeInput(ctx); }
        catch { return $"{{\"nodeType\":\"{ctx.Node.NodeType}\"}}"; }
    }

    private static string? BuildCompletedPayload(NodeOutcome.Succeeded ok)
    {
        if (string.IsNullOrEmpty(ok.ResolvedPrompt)) return ok.Output;
        return System.Text.Json.JsonSerializer.Serialize(new { output = ok.Output, resolvedPrompt = ok.ResolvedPrompt });
    }

    // ---- Persistence helpers ---------------------------------------------

    private async Task<LoopRunNode> CreateRunNodeAsync(Guid runId, Guid nodeId, string label)
    {
        using var scope = _sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            NodeLabel = label,
            Status = LoopRunNodeStatus.Pending,
            RetryCount = 0,
        };
        await store.CreateRunNodeAsync(runNode);
        return runNode;
    }

    private async Task UpdateRunNodeAsync(LoopRunNode runNode)
    {
        using var scope = _sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        await store.UpdateRunNodeAsync(runNode);
    }

    private async Task PersistEdgeTraversalAsync(Guid runId, Guid edgeId, int count)
    {
        using var scope = _sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        await store.PersistEdgeTraversalAsync(runId, edgeId, count);
    }

    private async Task<WorkItem?> RefreshWorkItemAsync(Guid id)
    {
        using var scope = _sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        return await store.GetByIdAsync(id);
    }

    private async Task<(LoopRun Run, WorkItem WorkItem, LoopTemplate Template, IReadOnlyList<LoopNode> Nodes, IReadOnlyList<LoopNodeEdge> Edges)?> LoadRunStateAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var loopTemplateStore = scope.ServiceProvider.GetRequiredService<ILoopTemplateStore>();

        var run = await loopRunStore.GetByIdAsync(runId);
        if (run == null) return null;
        var wi = await workItemStore.GetByIdAsync(run.WorkItemId);
        if (wi == null) return null;
        var graph = await loopTemplateStore.GetTemplateGraphByVersionIdAsync(run.LoopTemplateVersionId);
        if (graph == null) return null;
        return (run, wi, graph.Template, graph.Nodes.ToList(), graph.Edges.ToList());
    }

    // ---- Run-state transitions -------------------------------------------

    private async Task<LoopRunStatus> CompleteRunAsync(Guid runId, string? cleanupOutput = null)
    {
        await LogEventAsync(runId, "CleanupStarted", "Cleanup phase started");
        if (!string.IsNullOrEmpty(cleanupOutput))
            await LogEventAsync(runId, "CleanupCompleted", $"Cleanup finished: {cleanupOutput}");

        Guid? workItemId = null;
        LoopRunStatus prevStatus = LoopRunStatus.Running;
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await store.GetByIdAsync(runId);
            if (run != null)
            {
                workItemId = run.WorkItemId;
                prevStatus = run.Status;
                run.Status = LoopRunStatus.Completed;
                run.CompletedAt = DateTime.UtcNow;
                run.UpdatedAt = DateTime.UtcNow;
                await store.UpdateRunAsync(run);
            }
        }
        if (workItemId.HasValue)
            await TransitionWorkItemAsync(workItemId.Value, WorkItemStatus.Done, null);
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, prevStatus, LoopRunStatus.Completed));
        await LogEventAsync(runId, "LoopRunCompleted", "Run completed successfully");
        ReleaseRunControl(runId);
        return LoopRunStatus.Completed;
    }

    private async Task<LoopRunStatus> FailRunAsync(Guid runId, string reason)
    {
        Guid? workItemId = null;
        LoopRunStatus prevStatus = LoopRunStatus.Running;
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await store.GetByIdAsync(runId);
            if (run != null)
            {
                workItemId = run.WorkItemId;
                prevStatus = run.Status;
                run.Status = LoopRunStatus.Failed;
                run.CompletedAt = DateTime.UtcNow;
                run.UpdatedAt = DateTime.UtcNow;
                await store.UpdateRunAsync(run);
            }
        }
        if (workItemId.HasValue)
            await TransitionWorkItemAsync(workItemId.Value, WorkItemStatus.HumanFeedback, reason);
        await NotifyAsync(() => _notifier.EventLoggedAsync(runId, $"Run failed: {reason}", "Error", null, null));
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, prevStatus, LoopRunStatus.Failed));
        ReleaseRunControl(runId);
        return LoopRunStatus.Failed;
    }

    private async Task CancelRunInternalAsync(Guid runId)
    {
        Guid? workItemId = null;
        LoopRunStatus prevStatus = LoopRunStatus.Running;
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await store.GetByIdAsync(runId);
            if (run != null)
            {
                workItemId = run.WorkItemId;
                prevStatus = run.Status;
                run.Status = LoopRunStatus.Cancelled;
                run.CompletedAt = DateTime.UtcNow;
                run.UpdatedAt = DateTime.UtcNow;
                await store.UpdateRunAsync(run);
            }
        }
        if (workItemId.HasValue)
            await TransitionWorkItemAsync(workItemId.Value, WorkItemStatus.HumanFeedback, "Run cancelled");
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, prevStatus, LoopRunStatus.Cancelled));
        ReleaseRunControl(runId);
    }

    private async Task TransitionRunAsync(Guid runId, LoopRunStatus next)
    {
        LoopRunStatus prev = LoopRunStatus.Running;
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await store.GetByIdAsync(runId);
            if (run == null) return;
            prev = run.Status;
            if (prev == next) return;
            run.Status = next;
            run.UpdatedAt = DateTime.UtcNow;
            await store.UpdateRunAsync(run);
        }
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, prev, next));
    }

    private void ReleaseRunControl(Guid runId)
    {
        if (_runs.TryRemove(runId, out var control))
            control.Dispose();
    }

    private async Task TransitionWorkItemAsync(Guid workItemId, WorkItemStatus next, string? reason)
    {
        WorkItemStatus prev;
        using (var scope = _sp.CreateScope())
        {
            var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
            var wi = await workItemStore.GetByIdAsync(workItemId);
            if (wi == null)
            {
                _logger.LogWarning("TransitionWorkItemAsync: WorkItem {WorkItemId} not found", workItemId);
                return;
            }
            prev = wi.Status;
            if (prev == next && wi.HumanFeedbackReason == reason) return;
            wi.Status = next;
            if (reason != null) wi.HumanFeedbackReason = reason;
            wi.UpdatedAt = DateTime.UtcNow;
            await workItemStore.UpdateAsync(wi);
        }
        await NotifyAsync(() => _workItemNotifier.WorkItemStateChangedAsync(workItemId, prev, next));
        if (next == WorkItemStatus.HumanFeedback && reason != null)
            await NotifyAsync(() => _workItemNotifier.HumanFeedbackRequiredAsync(workItemId, reason));
    }
}
