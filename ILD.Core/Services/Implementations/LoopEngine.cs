using System.Collections.Concurrent;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
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

    public async Task StartRunAsync(string workItemId, CancellationToken cancellationToken = default)
    {
        Guid runId;
        using (var scope = _sp.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var tracker = scope.ServiceProvider.GetRequiredService<IActiveWorkItemTracker>();
            var resolver = scope.ServiceProvider.GetRequiredService<Remote.ILoopTemplateResolver>();

            var wi = await manager.GetWorkItemAsync(workItemId)
                ?? throw new InvalidOperationException($"WorkItem {workItemId} not found");

            var existingRun = await loopRunStore.GetCurrentByWorkItemAsync(workItemId);
            if (existingRun != null)
            {
                tracker.Add(workItemId);

                if (existingRun.Status == LoopRunStatus.Running && wi.Status != RemoteWorkItemStatus.Running)
                {
                    await manager.TransitionAsync(
                        workItemId,
                        RemoteWorkItemStatus.Running,
                        currentLoopRunId: existingRun.Id);
                }

                throw new InvalidOperationException(
                    $"WorkItem {workItemId} already has a non-completed run ({existingRun.Id}) in status {existingRun.Status}.");
            }

            // Resolve the loop template from the work item's tags. This is the
            // single source of truth per PRD §3.7 — users no longer pick a
            // template explicitly. If the tags don't resolve to exactly one
            // template, escalate to HumanFeedback and return without starting
            // a run.
            var tags = wi.Tags;
            var resolution = resolver.Resolve(tags);
            if (resolution.Kind != Remote.LoopTemplateResolutionKind.Single || resolution.TemplateId is null)
            {
                var reason = resolution.Kind switch
                {
                    Remote.LoopTemplateResolutionKind.None => "No loop found for existing tags",
                    Remote.LoopTemplateResolutionKind.Ambiguous =>
                        $"Multiple loop templates match tags: {string.Join(", ", resolution.MatchingTemplateNames)}",
                    _ => "Unable to resolve template",
                };
                await manager.TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, reason);
                return;
            }

            var templateStore = scope.ServiceProvider.GetRequiredService<ILoopTemplateStore>();
            var version = await templateStore.GetLatestVersionAsync(resolution.TemplateId.Value)
                ?? throw new InvalidOperationException(
                    $"Loop template {resolution.TemplateId} has no published version");

            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItemId,
                LoopTemplateVersionId = version.Id,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.Running,
                StartedAt = DateTime.UtcNow,
                RepositoryId = wi.RepositoryId,
                CreatedByLoopRunId = wi.CreatedByLoopRunId,
            };
            await loopRunStore.CreateRunAsync(run);
            await manager.TransitionAsync(workItemId, RemoteWorkItemStatus.Running, currentLoopRunId: run.Id);
            tracker.Add(workItemId);
            runId = run.Id;
        }

        var control = _runs.GetOrAdd(runId, _ => new RunControl());
        control.Task = Task.Run(() => RunAsync(runId, control.Cts.Token), control.Cts.Token);
    }

    private static IReadOnlyList<string> ParseLocalTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return Array.Empty<string>();
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<List<string>>(tagsJson);
            return arr ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
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

    public async Task RetryFromNodeAsync(Guid runId, Guid runNodeId)
    {
        // Refuse if a run loop is already executing for this run.
        if (_runs.TryGetValue(runId, out var existing) && existing.Task is { IsCompleted: false })
            throw new InvalidOperationException("Run is currently executing; pause or cancel it before retrying a node.");

        var loaded = await LoadRunStateAsync(runId);
        if (loaded == null)
            throw new InvalidOperationException($"Run {runId} not found or template missing");
        var (run, workItem, template, nodes, edges) = loaded.Value;

        LoopRunNode? targetRunNode;
        IReadOnlyList<LoopRunNode> allRunNodes;
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            targetRunNode = await store.GetRunNodeByIdAsync(runNodeId);
            allRunNodes = await store.GetRunNodesAsync(runId);
        }
        if (targetRunNode == null || targetRunNode.LoopRunId != runId)
            throw new InvalidOperationException($"Run node {runNodeId} not found on run {runId}");

        var targetTemplateNode = nodes.FirstOrDefault(n => n.Id == targetRunNode.LoopNodeId);
        if (targetTemplateNode == null)
            throw new InvalidOperationException($"Template node {targetRunNode.LoopNodeId} not found in graph");

        // Reconstruct the {{PreviousNode.Output}} that the target saw last
        // time it started. Find candidate source nodes via incoming edges,
        // then pick the most recent prior LoopRunNode (by CreatedAt) whose
        // LoopNodeId is one of those sources, restricted to runs created
        // strictly before the target visit.
        var incomingSourceIds = edges
            .Where(e => e.TargetNodeId == targetTemplateNode.Id)
            .Select(e => e.SourceNodeId)
            .ToHashSet();
        string? prevOutputSeed = null;
        var predecessor = allRunNodes
            .Where(rn => rn.CreatedAt < targetRunNode.CreatedAt && incomingSourceIds.Contains(rn.LoopNodeId))
            .OrderByDescending(rn => rn.CreatedAt)
            .FirstOrDefault();
        if (predecessor != null) prevOutputSeed = predecessor.Output;

        // Reset run state so the loop runs forward from this node.
        using (var scope = _sp.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var manager = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
            var runEntity = await store.GetByIdAsync(runId);
            if (runEntity != null)
            {
                runEntity.Status = LoopRunStatus.Running;
                runEntity.CurrentNodeId = targetTemplateNode.Id;
                runEntity.CompletedAt = null;
                runEntity.UpdatedAt = DateTime.UtcNow;
                await store.UpdateRunAsync(runEntity);
                run = runEntity;
            }
            await manager.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.Running);
            workItem = await manager.GetWorkItemAsync(run.WorkItemId) ?? workItem;
        }

        await LogEventAsync(runId, "RetryFromNode",
            $"Retry from node {targetTemplateNode.Label}", targetTemplateNode.Id, runNodeId: runNodeId);

        // Replace any stale RunControl from a previous execution.
        if (_runs.TryRemove(runId, out var stale)) stale.Dispose();
        var control = _runs.GetOrAdd(runId, _ => new RunControl());

        var maxNodeExecs = template.MaxNodeExecutions > 0 ? template.MaxNodeExecutions : 200;
        var maxWallHours = template.MaxWallClockHours > 0 ? template.MaxWallClockHours : 24;
        var deadline = DateTime.UtcNow.AddHours(maxWallHours);

        control.Task = Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(control.Cts.Token);
            try
            {
                var seedOutputs = new Dictionary<Guid, string?>();
                Guid? seedIncomingSource = null;
                if (predecessor != null)
                {
                    seedOutputs[predecessor.LoopNodeId] = prevOutputSeed;
                    seedIncomingSource = predecessor.LoopNodeId;
                }
                await ExecuteRunLoopAsync(
                    runId, run, workItem, nodes, edges, targetTemplateNode,
                    seedOutputs, new Dictionary<Guid, int>(),
                    control, deadline, maxNodeExecs,
                    initialIncomingSource: seedIncomingSource, linkedCts.Token);
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            catch (Exception ex)
            {
                await NotifyAsync(() => _notifier.EventLoggedAsync(runId, $"Retry failed: {ex.Message}", "Error", null, null));
            }
        }, control.Cts.Token);
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
            var manager = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();

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

                await manager.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.Running);
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

        // Resolve where to begin execution. If the run is parked at a node,
        // continue routing from there (the runnode's terminal status decides
        // the edge to follow). Otherwise begin at the entry node.
        LoopNode current;
        Guid? incomingSource = null;
        var resume = await ResolveResumePointAsync(run, nodes);
        switch (resume)
        {
            case ResumePoint.StartFromEntry e:
                current = e.Node;
                break;
            case ResumePoint.RouteFrom rf:
                outputBySource[rf.Node.Id] = rf.Output;
                if (rf.EdgeType == EdgeType.OnSuccess && IsTerminal(rf.Node))
                    return await CompleteRunAsync(runId);
                var (nextResume, _, terminal) = TryRoute(rf.Node, rf.EdgeType, edges, traversalCounts);
                if (terminal != null) return await terminal(runId);
                if (nextResume == null)
                {
                    await LogRoutingErrorAsync(runId, rf.Node, edges, rf.EdgeType);
                    var edgeTypeName = rf.EdgeType switch
                    {
                        EdgeType.OnSuccess => "on_success",
                        EdgeType.OnFailure => "on_failure",
                        EdgeType.OnRespond => "on_respond",
                        _ => rf.EdgeType.ToString().ToLowerInvariant(),
                    };
                    var failMsg = rf.EdgeType switch
                    {
                        EdgeType.OnSuccess => $"Node {rf.Node.Label} succeeded but has no outgoing on_success edge",
                        EdgeType.OnFailure => "Retries exhausted with no on_failure edge",
                        EdgeType.OnRespond => $"Node {rf.Node.Label} responded but has no outgoing on_respond edge",
                        _ => $"Node {rf.Node.Label} has no outgoing {edgeTypeName} edge",
                    };
                    await TransitionWorkItemAsync(workItem.Id, RemoteWorkItemStatus.HumanFeedback,
                        rf.EdgeType == EdgeType.OnFailure ? HumanFeedbackReasons.NodeFailed : $"No {edgeTypeName} edge");
                    return await FailRunAsync(runId, failMsg);
                }
                await PersistEdgeTraversalAsync(runId, nextResume.EdgeId, traversalCounts[nextResume.EdgeId]);
                current = nodes.First(n => n.Id == nextResume.NextNode.Id);
                incomingSource = rf.Node.Id;
                break;
            case ResumePoint.StillWaiting:
                // Run is parked and the signal hasn't arrived yet; remain in
                // WaitingHuman status. Do not consume the run-loop slot.
                return LoopRunStatus.WaitingHuman;
            default:
                return await FailRunAsync(runId, "No Start node");
        }

        return await ExecuteRunLoopAsync(
            runId, run, workItem, nodes, edges, current,
            outputBySource, traversalCounts, control, deadline, maxNodeExecs,
            initialIncomingSource: incomingSource, ct);
    }

    /// <summary>
    /// Drive the run forward from <paramref name="current"/>. The
    /// <paramref name="initialIncomingSource"/> identifies the template node
    /// whose output slot in <paramref name="outputBySource"/> should seed
    /// <c>{{PreviousNode.Output}}</c> for the very first iteration. As we
    /// route forward we track the incoming-edge source explicitly so the
    /// rendered prompt always reflects the edge actually traversed (matters
    /// when a node has multiple inbound edges, e.g. a loop-back).
    /// </summary>
    private async Task<LoopRunStatus> ExecuteRunLoopAsync(
        Guid runId,
        LoopRun run,
        WorkItemView workItem,
        IReadOnlyList<LoopNode> nodes,
        IReadOnlyList<LoopNodeEdge> edges,
        LoopNode current,
        Dictionary<Guid, string?> outputBySource,
        Dictionary<Guid, int> traversalCounts,
        RunControl control,
        DateTime deadline,
        int maxNodeExecs,
        Guid? initialIncomingSource,
        CancellationToken ct)
    {
        var executed = 0;
        var incomingSource = initialIncomingSource;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (control.IsPaused)
                {
                    if (DateTime.UtcNow > deadline)
                        return await FailRunAsync(runId, $"Exceeded MaxWallClockHours");
                    try { await Task.Delay(50, ct); } catch (OperationCanceledException) { }
                    continue;
                }

                if (++executed > maxNodeExecs)
                    return await FailRunAsync(runId, $"Exceeded MaxNodeExecutions={maxNodeExecs}");
                if (DateTime.UtcNow > deadline)
                    return await FailRunAsync(runId, $"Exceeded MaxWallClockHours");

                // Refresh workitem (other nodes may have mutated worktree path etc.)
                workItem = await RefreshWorkItemAsync(workItem.Id) ?? workItem;

                // {{PreviousNode.Output}} for `current` is the output of the
                // node whose outgoing edge we just traversed.
                string? prevOutput = incomingSource is { } src && outputBySource.TryGetValue(src, out var po)
                    ? po
                    : null;

                var outcome = await ExecuteNodeWithRetryAsync(run, workItem, current, prevOutput, edges, ct);

                switch (outcome)
                {
                    case NodeOutcome.Suspended s:
                        await TransitionRunAsync(runId, LoopRunStatus.WaitingHuman);
                        var availableActions = string.Join(",",
                            edges.Where(e => e.SourceNodeId == current.Id).Select(e => e.EdgeType.ToString()).Distinct());
                        var baseReason = ReasonForSuspend(s.Kind);
                        // Include the previous node's output in the conversation
                        // so the human sees the AI's actual response, not just
                        // a generic "Human Input Needed" label.
                        var conversationContent = !string.IsNullOrWhiteSpace(prevOutput)
                            ? prevOutput.Trim()
                            : baseReason;
                        await TransitionWorkItemAsync(workItem.Id, RemoteWorkItemStatus.HumanFeedback, conversationContent, availableActions, baseReason);
                        return LoopRunStatus.WaitingHuman;

                    case NodeOutcome.Terminal t:
                        return await CompleteRunAsync(runId, t.Output);

                    case NodeOutcome.Succeeded ok:
                        outputBySource[current.Id] = ok.Output;
                        var (nextOk, _, _) = TryRoute(current, EdgeType.OnSuccess, edges, traversalCounts);
                        if (nextOk == null)
                            return await FailRunAsync(runId, $"Node {current.Label} succeeded but has no outgoing on_success edge");
                        if (nextOk.MaxExceeded)
                            return await FailRunAsync(runId, $"Edge exceeded max traversals ({nextOk.MaxTraversals})");
                        await PersistEdgeTraversalAsync(runId, nextOk.EdgeId, traversalCounts[nextOk.EdgeId]);
                        incomingSource = current.Id;
                        current = nodes.First(n => n.Id == nextOk.NextNode.Id);
                        break;

                    case NodeOutcome.Failed f:
                        outputBySource[current.Id] = f.Output;
                        var (nextFail, _, _) = TryRoute(current, EdgeType.OnFailure, edges, traversalCounts);
                        if (nextFail == null)
                        {
                            await LogRoutingErrorAsync(runId, current, edges, EdgeType.OnFailure);
                            await TransitionWorkItemAsync(workItem.Id, RemoteWorkItemStatus.HumanFeedback, HumanFeedbackReasons.NodeFailed);
                            return await FailRunAsync(runId, "Retries exhausted with no on_failure edge");
                        }
                        if (nextFail.MaxExceeded)
                            return await FailRunAsync(runId, $"Edge exceeded max traversals ({nextFail.MaxTraversals})");
                        await PersistEdgeTraversalAsync(runId, nextFail.EdgeId, traversalCounts[nextFail.EdgeId]);
                        incomingSource = current.Id;
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
        public sealed record RouteFrom(LoopNode Node, EdgeType EdgeType, string? Output) : ResumePoint;
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
                    if (rn.Status == LoopRunNodeStatus.Succeeded) return new ResumePoint.RouteFrom(current, EdgeType.OnSuccess, rn.Output);
                    if (rn.Status == LoopRunNodeStatus.Failed) return new ResumePoint.RouteFrom(current, EdgeType.OnFailure, rn.Output);
                    if (rn.Status == LoopRunNodeStatus.Responded) return new ResumePoint.RouteFrom(current, EdgeType.OnRespond, rn.Output);
                }
            }
        }
        var start = nodes.FirstOrDefault(n => n.NodeType == NodeType.Start);
        return start == null ? new ResumePoint.NoEntry() : new ResumePoint.StartFromEntry(start);
    }

    private static bool IsTerminal(LoopNode node) => node.NodeType == NodeType.Cleanup;

    /// <summary>
    /// Map a <see cref="SuspendKind"/> to the canonical
    /// <see cref="WorkItem.HumanFeedbackReason"/> string that the frontend
    /// keys its UI affordances off. The executor's own <c>Reason</c> stays
    /// in the event log for diagnostics.
    /// </summary>
    private static string ReasonForSuspend(SuspendKind kind) => kind switch
    {
        SuspendKind.HumanInput => HumanFeedbackReasons.HumanInputNeeded,
        SuspendKind.ExternalSignal => HumanFeedbackReasons.PrAwaitingMerge,
        _ => HumanFeedbackReasons.HumanInputNeeded,
    };

    private sealed record RouteResult(LoopNode NextNode, Guid EdgeId, int MaxTraversals, bool MaxExceeded);

    /// <summary>
    /// Pure routing decision: given a node's outcome edge type, pick the edge to
    /// follow and bump the in-memory traversal counter. Returns null when
    /// there is no edge of the requested type.
    /// </summary>
    private static (RouteResult? Route, LoopNodeEdge? Edge, Func<Guid, Task<LoopRunStatus>>? TerminalAction) TryRoute(
        LoopNode current, EdgeType wantedType, IReadOnlyList<LoopNodeEdge> edges, Dictionary<Guid, int> traversalCounts)
    {
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

    private async Task LogRoutingErrorAsync(Guid runId, LoopNode current, IReadOnlyList<LoopNodeEdge> edges, EdgeType wantedType)
    {
        if (wantedType == EdgeType.OnSuccess) return; // success-with-no-edge isn't a routing error per se; fail-fast in caller
        var edgeTypeName = wantedType switch
        {
            EdgeType.OnSuccess => "on_success",
            EdgeType.OnFailure => "on_failure",
            EdgeType.OnRespond => "on_respond",
            _ => wantedType.ToString().ToLowerInvariant(),
        };
        await LogEventAsync(runId, "RoutingError",
            $"Node {current.Label} ({current.Id}) has no {edgeTypeName} edge. " +
            $"All edges from this node: {string.Join(", ", edges.Where(e => e.SourceNodeId == current.Id).Select(e => $"edge={e.Id} type={e.EdgeType}"))}");
    }

    // ---- Per-node execution ----------------------------------------------

    private async Task<NodeOutcome> ExecuteNodeWithRetryAsync(LoopRun run, WorkItemView wi, LoopNode node, string? prevOutput, IReadOnlyList<LoopNodeEdge> edges, CancellationToken ct)
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

            // Persist the effective input (template-level description) so the UI
            // has something to show immediately. It is updated with resolved data
            // after execution below.
            runNode.EffectiveInput = effectiveInput;
            await UpdateRunNodeAsync(runNode);

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
                    if (!string.IsNullOrEmpty(s.ResolvedPrompt))
                        runNode.EffectiveInput = MergeResolvedPrompt(runNode.EffectiveInput, s.ResolvedPrompt);
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
                    // Update effective input with resolved prompt (after template transformation)
                    if (!string.IsNullOrEmpty(ok.ResolvedPrompt))
                        runNode.EffectiveInput = MergeResolvedPrompt(runNode.EffectiveInput, ok.ResolvedPrompt);
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

    private NodeExecutionContext BuildContext(LoopRun run, LoopRunNode runNode, LoopNode node, WorkItemView wi, string? prevOutput, CancellationToken ct)
    {
        Func<string, Task> progress = async (line) =>
        {
            if (!string.IsNullOrWhiteSpace(line))
                await NotifyAsync(() => _notifier.NodeProgressAsync(run.Id, node.Id, line));
        };
        return new NodeExecutionContext(run, runNode, node, wi, prevOutput, ct, progress);
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

    /// <summary>
    /// Merge a resolved prompt into an effective-input JSON payload.
    /// Parses the existing JSON (from <c>DescribeInput</c>), adds
    /// <c>resolvedPrompt</c>, and re-serialises. If the input is not valid
    /// JSON falls back to a plain object with the resolved prompt.
    /// </summary>
    private static string MergeResolvedPrompt(string? existing, string resolvedPrompt)
    {
        try
        {
            string result;
            using (var ms = new System.IO.MemoryStream())
            {
                using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    using var doc = System.Text.Json.JsonDocument.Parse(existing ?? "{}");
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name != "resolvedPrompt")
                            prop.WriteTo(writer);
                    }
                    writer.WriteString("resolvedPrompt", resolvedPrompt);
                    writer.WriteEndObject();
                    writer.Flush();
                }
                result = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            return result;
        }
        catch (System.Text.Json.JsonException)
        {
            return System.Text.Json.JsonSerializer.Serialize(new { nodeType = "unknown", resolvedPrompt });
        }
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

    private async Task<WorkItemView?> RefreshWorkItemAsync(string id)
    {
        using var scope = _sp.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
        return await manager.GetWorkItemAsync(id);
    }

    private async Task<(LoopRun Run, WorkItemView WorkItem, LoopTemplate Template, IReadOnlyList<LoopNode> Nodes, IReadOnlyList<LoopNodeEdge> Edges)?> LoadRunStateAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var manager = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
        var loopTemplateStore = scope.ServiceProvider.GetRequiredService<ILoopTemplateStore>();

        var run = await loopRunStore.GetByIdAsync(runId);
        if (run == null) return null;
        var wi = await manager.GetWorkItemAsync(run.WorkItemId);
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

        string? workItemId = null;
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
        if (!string.IsNullOrEmpty(workItemId))
            await TransitionWorkItemAsync(workItemId, RemoteWorkItemStatus.Done, null);
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, prevStatus, LoopRunStatus.Completed));
        await LogEventAsync(runId, "LoopRunCompleted", "Run completed successfully");
        ReleaseRunControl(runId);
        return LoopRunStatus.Completed;
    }

    private async Task<LoopRunStatus> FailRunAsync(Guid runId, string reason)
    {
        string? workItemId = null;
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
        if (!string.IsNullOrEmpty(workItemId))
            await TransitionWorkItemAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, reason);
        await NotifyAsync(() => _notifier.EventLoggedAsync(runId, $"Run failed: {reason}", "Error", null, null));
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, prevStatus, LoopRunStatus.Failed));
        ReleaseRunControl(runId);
        return LoopRunStatus.Failed;
    }

    private async Task CancelRunInternalAsync(Guid runId)
    {
        string? workItemId = null;
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
        if (!string.IsNullOrEmpty(workItemId))
            await TransitionWorkItemAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, "Run cancelled");
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

    private async Task TransitionWorkItemAsync(string workItemId, RemoteWorkItemStatus next, string? reason, string? actions = null, string? humanFeedbackReason = null)
    {
        using var scope = _sp.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();
        await manager.TransitionAsync(workItemId, next, reason, actions, humanFeedbackReason: humanFeedbackReason);
    }
}
