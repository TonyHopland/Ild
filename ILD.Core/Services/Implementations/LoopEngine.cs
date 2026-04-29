using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations;

public class LoopEngine : ILoopEngine
{
    private readonly IServiceProvider _sp;
    private readonly INodeExecutorRegistry _registry;
    private readonly IRunNotifier _notifier;
    private readonly ConcurrentDictionary<Guid, RunControl> _runs = new();

    private sealed class RunControl
    {
        public CancellationTokenSource Cts { get; } = new();
        public bool IsPaused { get; set; }
        public Task? Task { get; set; }
    }

    public LoopEngine(IServiceProvider sp, INodeExecutorRegistry registry, IRunNotifier notifier)
    {
        _sp = sp;
        _registry = registry;
        _notifier = notifier;
    }

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
                RecoveryPolicy = "AutoResume",
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

    public Task PauseRunAsync(Guid runId)
    {
        if (_runs.TryGetValue(runId, out var control)) control.IsPaused = true;
        return _notifier.PausedAsync(runId);
    }

    public Task ResumeRunAsync(Guid runId)
    {
        if (_runs.TryGetValue(runId, out var control)) control.IsPaused = false;
        return _notifier.ResumedAsync(runId);
    }

    public Task CancelRunAsync(Guid runId)
    {
        if (_runs.TryGetValue(runId, out var control)) control.Cts.Cancel();
        return Task.CompletedTask;
    }

    public async Task<LoopRunStatus> GetRunStatusAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        return run?.Status ?? LoopRunStatus.Failed;
    }

    public Task<IEnumerable<Guid>> GetActiveRunIdsAsync()
        => Task.FromResult<IEnumerable<Guid>>(_runs.Keys.ToList());

    public async Task<LoopRunStatus> RunAsync(Guid runId, CancellationToken externalCt = default)
    {
        var control = _runs.GetOrAdd(runId, _ => new RunControl());
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, control.Cts.Token);
        var ct = linkedCts.Token;

        LoopRun run;
        WorkItem workItem;
        LoopTemplateVersion version;
        LoopTemplate template;
        List<LoopNode> nodes;
        List<LoopNodeEdge> edges;

        using (var scope = _sp.CreateScope())
        {
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
            var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();
            var loopTemplateStore = scope.ServiceProvider.GetRequiredService<ILoopTemplateStore>();

            run = await loopRunStore.GetByIdAsync(runId)
                ?? throw new InvalidOperationException($"Run {runId} not found");
            workItem = await workItemStore.GetByIdAsync(run.WorkItemId)
                ?? throw new InvalidOperationException($"WorkItem {run.WorkItemId} not found");
            version = await loopTemplateStore.GetVersionByIdAsync(run.LoopTemplateVersionId)
                ?? throw new InvalidOperationException($"Version {run.LoopTemplateVersionId} not found");
            template = await providerStore.GetLoopTemplateByVersionIdAsync(run.LoopTemplateVersionId)
                ?? throw new InvalidOperationException($"Template for version {run.LoopTemplateVersionId} not found");
            nodes = (await loopRunStore.GetNodesForVersionAsync(run.LoopTemplateVersionId)).ToList();
            var nodeIds = nodes.Select(n => n.Id).ToList();
            edges = (await loopRunStore.GetEdgesForNodeIdsAsync(nodeIds)).ToList();
        }

        var startNode = nodes.FirstOrDefault(n => n.NodeType == NodeType.Start);
        if (startNode == null)
            return await FailRunAsync(runId, "No Start node");

        var maxNodeExecs = template.MaxNodeExecutions > 0 ? template.MaxNodeExecutions : 200;
        var maxWallHours = template.MaxWallClockHours > 0 ? template.MaxWallClockHours : 24;
        var deadline = (run.StartedAt ?? DateTime.UtcNow).AddHours(maxWallHours);

        var traversalCounts = new Dictionary<Guid, int>();
        string? previousOutput = null;
        var current = startNode;
        var executed = 0;

        try
        {
            while (true)
            {
                if (run.CurrentNodeId.HasValue)
                {
                    var currentLoopNode = nodes.FirstOrDefault(n => n.Id == run.CurrentNodeId);
                    if (currentLoopNode?.NodeType == NodeType.PR)
                    {
                        LoopRunNode? existingRunNode = null;
                        using (var scope = _sp.CreateScope())
                        {
                            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                            existingRunNode = await loopRunStore.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value);
                        }
                        if (existingRunNode != null &&
                            (existingRunNode.Status == LoopRunNodeStatus.Succeeded ||
                             existingRunNode.Status == LoopRunNodeStatus.Failed))
                        {
                            current = currentLoopNode;
                            previousOutput = existingRunNode.Output;
                            var prSuccess = existingRunNode.Status == LoopRunNodeStatus.Succeeded;

                            if (prSuccess && current.NodeType == NodeType.Cleanup)
                                return await CompleteRunAsync(runId);

                            var prWantedType = prSuccess ? EdgeType.OnSuccess : EdgeType.OnFailure;
                            var prEdge = edges.FirstOrDefault(e => e.SourceNodeId == current.Id && e.EdgeType == prWantedType);

                            if (prEdge == null && !prSuccess)
                            {
                                await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, "PR rejected; no on_failure edge");
                                return await FailRunAsync(runId, "PR rejected with no on_failure edge");
                            }
                            if (prEdge == null && prSuccess)
                                return await FailRunAsync(runId, $"Node {current.Label} succeeded but has no outgoing on_success edge");

                            traversalCounts.TryGetValue(prEdge!.Id, out var prTraversed);
                            if (prEdge.MaxTraversals.HasValue && prTraversed >= prEdge.MaxTraversals.Value)
                                return await FailRunAsync(runId, $"Edge exceeded max traversals ({prEdge.MaxTraversals})");

                            traversalCounts[prEdge.Id] = prTraversed + 1;
                            await PersistEdgeTraversalAsync(runId, prEdge.Id, traversalCounts[prEdge.Id]);
                            current = nodes.First(n => n.Id == prEdge.TargetNodeId);
                            run.CurrentNodeId = null;
                            continue;
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();
                while (control.IsPaused)
                {
                    if (ct.IsCancellationRequested) break;
                    await Task.Delay(50, CancellationToken.None);
                    if (ct.IsCancellationRequested) break;
                }
                ct.ThrowIfCancellationRequested();

                if (++executed > maxNodeExecs)
                    return await FailRunAsync(runId, $"Exceeded MaxNodeExecutions={maxNodeExecs}");
                if (DateTime.UtcNow > deadline)
                    return await FailRunAsync(runId, $"Exceeded MaxWallClockHours={maxWallHours}");

                var outcome = await ExecuteNodeWithRetryAsync(run, workItem, current, previousOutput, ct);

                if (outcome.Status == LoopRunNodeStatus.WaitingHuman)
                {
                    var reason = current.NodeType == NodeType.PR
                        ? "PR awaiting merge"
                        : "Human node awaiting input";
                    await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, reason);
                    return LoopRunStatus.Running;
                }

                previousOutput = outcome.Output;
                var success = outcome.Status == LoopRunNodeStatus.Succeeded;

                if (success && current.NodeType == NodeType.Cleanup)
                    return await CompleteRunAsync(runId);

                var wantedType = success ? EdgeType.OnSuccess : EdgeType.OnFailure;
                var edge = edges.FirstOrDefault(e => e.SourceNodeId == current.Id && e.EdgeType == wantedType);

                if (edge == null && !success)
                {
                    await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, "Node failed; no on_failure edge and retries exhausted");
                    return await FailRunAsync(runId, "Retries exhausted with no on_failure edge");
                }
                if (edge == null && success)
                    return await FailRunAsync(runId, $"Node {current.Label} succeeded but has no outgoing on_success edge");

                traversalCounts.TryGetValue(edge!.Id, out var traversed);
                if (edge.MaxTraversals.HasValue && traversed >= edge.MaxTraversals.Value)
                    return await FailRunAsync(runId, $"Edge exceeded max traversals ({edge.MaxTraversals})");

                traversalCounts[edge.Id] = traversed + 1;
                await PersistEdgeTraversalAsync(runId, edge.Id, traversalCounts[edge.Id]);

                current = nodes.First(n => n.Id == edge.TargetNodeId);
            }
        }
        catch (OperationCanceledException)
        {
            await CancelRunInternalAsync(runId);
            return LoopRunStatus.Cancelled;
        }
    }

    private sealed record RunNodeOutcome(LoopRunNodeStatus Status, string? Output);

    private async Task<RunNodeOutcome> ExecuteNodeWithRetryAsync(LoopRun run, WorkItem wi, LoopNode node, string? prevOutput, CancellationToken ct)
    {
        var maxRetries = node.MaxRetries;
        var attempt = 0;

        while (true)
        {
            attempt++;
            LoopRunNode runNode;
            using (var scope = _sp.CreateScope())
            {
                var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                var runEntity = await loopRunStore.GetByIdAsync(run.Id);
                if (runEntity == null)
                    return new RunNodeOutcome(LoopRunNodeStatus.Failed, null);

                runNode = new LoopRunNode
                {
                    Id = Guid.NewGuid(),
                    LoopRunId = run.Id,
                    LoopNodeId = node.Id,
                    Status = LoopRunNodeStatus.Running,
                    RetryCount = attempt - 1,
                    StartedAt = DateTime.UtcNow,
                };
                await loopRunStore.CreateRunNodeAsync(runNode);
                runEntity.NodeExecutionCount++;
                runEntity.CurrentNodeId = node.Id;
                await loopRunStore.UpdateRunAsync(runEntity);
            }
            await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running);

            var ctx = new NodeExecutionContext(run, runNode, node, wi, prevOutput, ct);

            if (node.NodeType == NodeType.Human)
            {
                runNode.Status = LoopRunNodeStatus.WaitingHuman;
                runNode.CompletedAt = DateTime.UtcNow;
                using (var scope = _sp.CreateScope())
                {
                    var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                    await loopRunStore.UpdateRunNodeAsync(runNode);
                }
                await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.WaitingHuman);
                return new RunNodeOutcome(LoopRunNodeStatus.WaitingHuman, null);
            }

            NodeExecutionResult execResult;
            try
            {
                var executor = _registry.Get(node.NodeType);
                execResult = await executor.ExecuteAsync(ctx);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                execResult = NodeExecutionResult.Fail(ex.Message);
            }

            if (node.NodeType == NodeType.PR && execResult.Success)
            {
                runNode.Status = LoopRunNodeStatus.WaitingHuman;
                runNode.Output = execResult.Output;
                runNode.CompletedAt = DateTime.UtcNow;
                using (var scope = _sp.CreateScope())
                {
                    var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                    await loopRunStore.UpdateRunNodeAsync(runNode);
                }
                await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.WaitingHuman);
                return new RunNodeOutcome(LoopRunNodeStatus.WaitingHuman, execResult.Output);
            }

            if (execResult.Success)
            {
                runNode.Status = LoopRunNodeStatus.Succeeded;
                runNode.Output = execResult.Output;
                runNode.CompletedAt = DateTime.UtcNow;
                using (var scope = _sp.CreateScope())
                {
                    var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                    await loopRunStore.UpdateRunNodeAsync(runNode);
                }
                await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded);
                return new RunNodeOutcome(LoopRunNodeStatus.Succeeded, execResult.Output);
            }

            runNode.Status = LoopRunNodeStatus.Failed;
            runNode.Error = execResult.Error;
            runNode.Output = execResult.Output;
            runNode.CompletedAt = DateTime.UtcNow;
            using (var scope = _sp.CreateScope())
            {
                var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                await loopRunStore.UpdateRunNodeAsync(runNode);
            }
            await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Failed);

            bool hasFailureEdge;
            using (var scope = _sp.CreateScope())
            {
                var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                hasFailureEdge = await loopRunStore.HasFailureEdgeAsync(node.Id);
            }
            if (hasFailureEdge)
                return new RunNodeOutcome(LoopRunNodeStatus.Failed, execResult.Output);

            if (attempt > maxRetries)
                return new RunNodeOutcome(LoopRunNodeStatus.Failed, execResult.Output);
        }
    }

    private async Task PersistEdgeTraversalAsync(Guid runId, Guid edgeId, int count)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        await loopRunStore.PersistEdgeTraversalAsync(runId, edgeId, count);
    }

    private async Task<LoopRunStatus> CompleteRunAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run != null)
        {
            run.Status = LoopRunStatus.Completed;
            run.CompletedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;
            await loopRunStore.UpdateRunAsync(run);
        }
        await _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Completed);
        return LoopRunStatus.Completed;
    }

    private async Task<LoopRunStatus> FailRunAsync(Guid runId, string reason)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run != null)
        {
            run.Status = LoopRunStatus.Failed;
            run.CompletedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;
            await loopRunStore.UpdateRunAsync(run);
        }
        await _notifier.EventLoggedAsync(runId, $"Run failed: {reason}");
        await _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Failed);
        return LoopRunStatus.Failed;
    }

    private async Task CancelRunInternalAsync(Guid runId)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var run = await loopRunStore.GetByIdAsync(runId);
        if (run != null)
        {
            run.Status = LoopRunStatus.Cancelled;
            run.CompletedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;
            await loopRunStore.UpdateRunAsync(run);
        }
        await _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Cancelled);
    }

    private async Task TransitionWorkItemAsync(Guid workItemId, WorkItemStatus next, string? reason)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var wi = await workItemStore.GetByIdAsync(workItemId);
        if (wi == null) return;
        wi.Status = next;
        if (reason != null) wi.HumanFeedbackReason = reason;
        wi.UpdatedAt = DateTime.UtcNow;
        await workItemStore.UpdateAsync(wi);
    }

    public async Task SignalPrResultAsync(Guid runId, Guid prRunNodeId, bool merged)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var prRunNode = await loopRunStore.GetRunNodeByIdAsync(prRunNodeId);
        if (prRunNode == null) return;

        prRunNode.Status = merged ? LoopRunNodeStatus.Succeeded : LoopRunNodeStatus.Failed;
        prRunNode.CompletedAt = DateTime.UtcNow;
        prRunNode.Error = merged ? null : "PR rejected";
        await loopRunStore.UpdateRunNodeAsync(prRunNode);

        var run = await loopRunStore.GetByIdAsync(runId);
        if (run != null)
        {
            run.CurrentNodeId = prRunNode.LoopNodeId;
            run.Status = LoopRunStatus.Running;
            await loopRunStore.UpdateRunAsync(run);
        }

        var wi = run != null ? await workItemStore.GetByIdAsync(run.WorkItemId) : null;
        if (wi != null)
        {
            wi.Status = WorkItemStatus.Running;
            wi.HumanFeedbackReason = null;
            wi.UpdatedAt = DateTime.UtcNow;
            await workItemStore.UpdateAsync(wi);
        }

        await _notifier.NodeStateChangedAsync(runId, prRunNode.LoopNodeId, LoopRunNodeStatus.WaitingHuman, prRunNode.Status);
    }
}
