using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations;

public class LoopEngine : ILoopEngine
{
    private readonly Func<AppDbContext> _dbFactory;
    private readonly INodeExecutorRegistry _registry;
    private readonly IRunNotifier _notifier;
    private readonly ConcurrentDictionary<Guid, RunControl> _runs = new();

    private sealed class RunControl
    {
        public CancellationTokenSource Cts { get; } = new();
        public bool IsPaused { get; set; }
        public Task? Task { get; set; }
    }

    public LoopEngine(Func<AppDbContext> dbFactory, INodeExecutorRegistry registry, IRunNotifier notifier)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _notifier = notifier;
    }

    public async Task StartRunAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        Guid runId;
        await using (var db = _dbFactory())
        {
            var wi = await db.WorkItems.FindAsync(workItemId)
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
            db.LoopRuns.Add(run);
            wi.CurrentLoopRunId = run.Id;
            wi.Status = WorkItemStatus.Running;
            await db.SaveChangesAsync();
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
        await using var db = _dbFactory();
        var run = await db.LoopRuns.FindAsync(runId);
        return run?.Status ?? LoopRunStatus.Failed;
    }

    public Task<IEnumerable<Guid>> GetActiveRunIdsAsync()
        => Task.FromResult<IEnumerable<Guid>>(_runs.Keys.ToList());

    /// <summary>
    /// Drives a single loop run to completion. Public for tests and recovery.
    /// </summary>
    public async Task<LoopRunStatus> RunAsync(Guid runId, CancellationToken externalCt = default)
    {
        var control = _runs.GetOrAdd(runId, _ => new RunControl());
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, control.Cts.Token);
        var ct = linkedCts.Token;

        await using var loadDb = _dbFactory();
        var run = await loadDb.LoopRuns.FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new InvalidOperationException($"Run {runId} not found");
        var workItem = await loadDb.WorkItems.FirstAsync(w => w.Id == run.WorkItemId, ct);
        var version = await loadDb.LoopTemplateVersions
            .Include(v => v.LoopTemplate)
            .FirstAsync(v => v.Id == run.LoopTemplateVersionId, ct);
        var template = version.LoopTemplate;
        var nodes = await loadDb.LoopNodes
            .Where(n => n.LoopTemplateVersionId == run.LoopTemplateVersionId)
            .ToListAsync(ct);
        var nodeIds = nodes.Select(n => n.Id).ToList();
        var edges = await loadDb.LoopNodeEdges
            .Where(e => nodeIds.Contains(e.SourceNodeId))
            .ToListAsync(ct);

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
                    await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, "Human node awaiting input");
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
            await using var db = _dbFactory();
            var runEntity = await db.LoopRuns.FirstAsync(r => r.Id == run.Id, ct);
            var runNode = new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = run.Id,
                LoopNodeId = node.Id,
                Status = LoopRunNodeStatus.Running,
                RetryCount = attempt - 1,
                StartedAt = DateTime.UtcNow,
            };
            db.LoopRunNodes.Add(runNode);
            runEntity.NodeExecutionCount++;
            runEntity.CurrentNodeId = node.Id;
            await db.SaveChangesAsync(ct);
            await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running);

            var ctx = new NodeExecutionContext(runEntity, runNode, node, wi, prevOutput, ct);

            if (node.NodeType == NodeType.Human)
            {
                runNode.Status = LoopRunNodeStatus.WaitingHuman;
                runNode.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
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

            if (execResult.Success)
            {
                runNode.Status = LoopRunNodeStatus.Succeeded;
                runNode.Output = execResult.Output;
                runNode.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded);
                return new RunNodeOutcome(LoopRunNodeStatus.Succeeded, execResult.Output);
            }

            runNode.Status = LoopRunNodeStatus.Failed;
            runNode.Error = execResult.Error;
            runNode.Output = execResult.Output;
            runNode.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Failed);

            // If there's an on_failure edge, route immediately (no retry).
            await using var look = _dbFactory();
            var hasFailureEdge = await look.LoopNodeEdges
                .AnyAsync(e => e.SourceNodeId == node.Id && e.EdgeType == EdgeType.OnFailure, ct);
            if (hasFailureEdge)
                return new RunNodeOutcome(LoopRunNodeStatus.Failed, execResult.Output);

            if (attempt > maxRetries)
                return new RunNodeOutcome(LoopRunNodeStatus.Failed, execResult.Output);
        }
    }

    private async Task PersistEdgeTraversalAsync(Guid runId, Guid edgeId, int count)
    {
        await using var db = _dbFactory();
        var existing = await db.LoopRunEdgeTraversals.FirstOrDefaultAsync(t => t.LoopRunId == runId && t.EdgeId == edgeId);
        if (existing == null)
        {
            db.LoopRunEdgeTraversals.Add(new LoopRunEdgeTraversal
            {
                Id = Guid.NewGuid(),
                LoopRunId = runId,
                EdgeId = edgeId,
                TraversalCount = count,
            });
        }
        else
        {
            existing.TraversalCount = count;
        }
        await db.SaveChangesAsync();
    }

    private async Task<LoopRunStatus> CompleteRunAsync(Guid runId)
    {
        await using var db = _dbFactory();
        var run = await db.LoopRuns.FirstAsync(r => r.Id == runId);
        run.Status = LoopRunStatus.Completed;
        run.CompletedAt = DateTime.UtcNow;
        run.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Completed);
        return LoopRunStatus.Completed;
    }

    private async Task<LoopRunStatus> FailRunAsync(Guid runId, string reason)
    {
        await using var db = _dbFactory();
        var run = await db.LoopRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run != null)
        {
            run.Status = LoopRunStatus.Failed;
            run.CompletedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        await _notifier.EventLoggedAsync(runId, $"Run failed: {reason}");
        await _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Failed);
        return LoopRunStatus.Failed;
    }

    private async Task CancelRunInternalAsync(Guid runId)
    {
        await using var db = _dbFactory();
        var run = await db.LoopRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run != null)
        {
            run.Status = LoopRunStatus.Cancelled;
            run.CompletedAt = DateTime.UtcNow;
            run.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        await _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Cancelled);
    }

    private async Task TransitionWorkItemAsync(Guid workItemId, WorkItemStatus next, string? reason)
    {
        await using var db = _dbFactory();
        var wi = await db.WorkItems.FindAsync(workItemId);
        if (wi == null) return;
        wi.Status = next;
        if (reason != null) wi.HumanFeedbackReason = reason;
        wi.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
