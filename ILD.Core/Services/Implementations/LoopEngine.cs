using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ILD.Core.Services.Implementations;

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

    private async Task LogEventAsync(Guid runId, string eventType, string message, Guid? nodeId = null, string? output = null, Guid? runNodeId = null)
    {
        var data = string.IsNullOrEmpty(output) ? message : $"{message}\n{output}";
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
        // Broadcast to SignalR clients (best-effort, wrapped by NotifyAsync)
        await NotifyAsync(() => _notifier.EventLoggedAsync(runId, data, eventType, nodeId, runNodeId));
    }

    private async Task NotifyAsync(Func<Task> notify)
    {
        try
        {
            await notify();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR notification failed (best-effort, not failing execution)");
        }
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
            try
            {
                await RunAsync(runId, control.Cts.Token);
            }
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
            var loopTemplateStore = scope.ServiceProvider.GetRequiredService<ILoopTemplateStore>();

            run = await loopRunStore.GetByIdAsync(runId)
                ?? throw new InvalidOperationException($"Run {runId} not found");
            workItem = await workItemStore.GetByIdAsync(run.WorkItemId)
                ?? throw new InvalidOperationException($"WorkItem {run.WorkItemId} not found");
            var graph = await loopTemplateStore.GetTemplateGraphByVersionIdAsync(run.LoopTemplateVersionId)
                ?? throw new InvalidOperationException($"Template graph for version {run.LoopTemplateVersionId} not found");
            template = graph.Template;
            version = graph.Version;
            nodes = graph.Nodes.ToList();
            edges = graph.Edges.ToList();
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
                    if (currentLoopNode?.NodeType == NodeType.PR || currentLoopNode?.NodeType == NodeType.Human)
                    {
                        LoopRunNode? existingRunNode = null;
                        using (var scope = _sp.CreateScope())
                        {
                            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                            existingRunNode = await loopRunStore.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value);
                        }
                        if (existingRunNode != null &&
                            existingRunNode.Status == LoopRunNodeStatus.WaitingHuman)
                        {
                            await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback,
                                currentLoopNode.NodeType == NodeType.PR
                                    ? "PR awaiting merge"
                                    : "Human node awaiting input");
                            return LoopRunStatus.Running;
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
                                await LogEventAsync(runId, "RoutingError",
                                    $"Node {current.Label} ({current.Id}) failed but has no on_failure edge. " +
                                    $"All edges from this node: {string.Join(", ", edges.Where(e => e.SourceNodeId == current.Id).Select(e => $"edge={e.Id} type={e.EdgeType}"))}");
                                await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, "PR rejected; no on_failure edge");
                                return await FailRunAsync(runId, "PR rejected with no on_failure edge");
                            }
                            if (prEdge == null && prSuccess)
                                return await FailRunAsync(runId, $"Node {current.Label} succeeded but has no outgoing on_success edge");

                            // prEdge is not-null here: the two `prEdge == null` branches above both `return`.
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
                    if (DateTime.UtcNow > deadline)
                        return await FailRunAsync(runId, $"Exceeded MaxWallClockHours={maxWallHours}");
                    try
                    {
                        await Task.Delay(50, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
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
                    return await CompleteRunAsync(runId, previousOutput);

                var wantedType = success ? EdgeType.OnSuccess : EdgeType.OnFailure;
                var edge = edges.FirstOrDefault(e => e.SourceNodeId == current.Id && e.EdgeType == wantedType);

                if (edge == null && !success)
                {
                    await LogEventAsync(runId, "RoutingError",
                        $"Node {current.Label} ({current.Id}) failed but has no on_failure edge. " +
                        $"All edges from this node: {string.Join(", ", edges.Where(e => e.SourceNodeId == current.Id).Select(e => $"edge={e.Id} type={e.EdgeType}"))}");
                    await TransitionWorkItemAsync(workItem.Id, WorkItemStatus.HumanFeedback, "Node failed; no on_failure edge and retries exhausted");
                    return await FailRunAsync(runId, "Retries exhausted with no on_failure edge");
                }
                if (edge == null && success)
                    return await FailRunAsync(runId, $"Node {current.Label} succeeded but has no outgoing on_success edge");

                // edge is not-null here: the two `edge == null` branches above both `return`.
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

    private static string BuildEffectiveInputJson(LoopNode node, WorkItem wi, string? previousNodeOutput, IReadOnlyList<string>? priorEventLogSummary)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        Dictionary<string, object?> payload = new();
        payload["nodeType"] = node.NodeType.ToString();

        try
        {
            var cfg = string.IsNullOrEmpty(node.Config)
                ? (Dictionary<string, object>?)null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(node.Config);

            switch (node.NodeType)
            {
                case NodeType.Cmd:
                    payload["command"] = cfg?.GetValueOrDefault("command")?.ToString() ?? "(no command)";
                    break;

                case NodeType.AI:
                {
                    var initialPrompt = cfg?.GetValueOrDefault("initialPrompt")?.ToString() ?? "";
                    var loopPrompt = cfg?.GetValueOrDefault("loopPrompt")?.ToString() ?? initialPrompt;
                    payload["prompt"] = initialPrompt;
                    payload["loopPrompt"] = loopPrompt;

                    var ctx = new Dictionary<string, object?>
                    {
                        ["workItemTitle"] = wi.Title,
                        ["workItemDescription"] = wi.Description,
                        ["previousNodeOutput"] = previousNodeOutput,
                    };
                    if (priorEventLogSummary != null && priorEventLogSummary.Count > 0)
                    {
                        ctx["priorEventLog"] = priorEventLogSummary;
                    }
                    payload["context"] = ctx;
                    break;
                }

                case NodeType.Human:
                    payload["prompt"] = cfg?.GetValueOrDefault("prompt")?.ToString() ?? "(no prompt)";
                    break;

                case NodeType.PR:
                    payload["prompt"] = cfg?.GetValueOrDefault("prompt")?.ToString() ?? "(no prompt)";
                    break;

                case NodeType.Start:
                    payload["message"] = "initialized";
                    break;

                case NodeType.Cleanup:
                    payload["message"] = "cleanup";
                    break;

                default:
                    payload["message"] = node.NodeType.ToString();
                    break;
            }
        }
        catch
        {
            payload["message"] = $"node-type={node.NodeType}";
        }

        return JsonSerializer.Serialize(payload, options);
    }

    private static string? BuildNodeCompletedOutput(NodeExecutionResult result)
    {
        if (!string.IsNullOrEmpty(result.ResolvedPrompt))
        {
            var dict = new Dictionary<string, object?>
            {
                ["output"] = result.Output,
                ["resolvedPrompt"] = result.ResolvedPrompt,
            };
            return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
        }
        return result.Output;
    }

    private async Task<RunNodeOutcome> ExecuteNodeWithRetryAsync(LoopRun run, WorkItem wi, LoopNode node, string? prevOutput, CancellationToken ct)
    {
        // PRD: error edge → follow immediately on failure; no error edge → auto-retry N times.
        // Decide once, before the retry loop.
        bool hasFailureEdge;
        using (var scope = _sp.CreateScope())
        {
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            hasFailureEdge = await loopRunStore.HasFailureEdgeAsync(node.Id);
        }
        var maxRetries = hasFailureEdge ? 0 : node.MaxRetries;
        var attempt = 0;

        // One LoopRunNode per execution; each visit to a template node creates a new row.
        LoopRunNode runNode = await CreateRunNodeAsync(run.Id, node.Id, node.Label);

        while (true)
        {
            attempt++;

            using (var scope = _sp.CreateScope())
            {
                var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                var runEntity = await loopRunStore.GetByIdAsync(run.Id);
                if (runEntity == null)
                    return new RunNodeOutcome(LoopRunNodeStatus.Failed, null);

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

            // Build effective input for observability (command/prompt/context the node will execute with)
            IReadOnlyList<string>? priorEventLogSummary = null;
            if (node.NodeType == NodeType.AI)
            {
                try
                {
                    using (var scope = _sp.CreateScope())
                    {
                        var eventLogSvc = scope.ServiceProvider.GetRequiredService<IEventLogService>();
                        var entries = await eventLogSvc.GetByRunIdAsync(run.Id);
                        priorEventLogSummary = entries.Select(e => $"{e.EventType}: {e.Data}").ToList();
                    }
                }
                catch { /* best-effort */ }
            }
            var effectiveInput = BuildEffectiveInputJson(node, wi, prevOutput, priorEventLogSummary);

            await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running));
            await LogEventAsync(run.Id, "NodeStarted", $"{node.Label} started", node.Id, effectiveInput, runNode.Id);

            Func<string, Task> safeProgress = line => NotifyAsync(() => _notifier.NodeProgressAsync(run.Id, node.Id, line));

            // Refresh the WorkItem from the store so executors see fields (e.g. WorktreePath, BranchName)
            // mutated by previous nodes in this run via their own scoped DbContexts.
            using (var scope = _sp.CreateScope())
            {
                var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
                var refreshed = await workItemStore.GetByIdAsync(wi.Id);
                if (refreshed != null) wi = refreshed;
            }

            var ctx = new NodeExecutionContext(run, runNode, node, wi, prevOutput, ct, safeProgress);

            if (node.NodeType == NodeType.Human)
            {
                runNode.Status = LoopRunNodeStatus.WaitingHuman;
                runNode.CompletedAt = DateTime.UtcNow;
                using (var scope = _sp.CreateScope())
                {
                    var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
                    await loopRunStore.UpdateRunNodeAsync(runNode);
                }
                await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.WaitingHuman));
                await LogEventAsync(run.Id, "HumanFeedbackRequested", $"{node.Label} waiting for human input", node.Id, runNodeId: runNode.Id);
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
                await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.WaitingHuman));
                await LogEventAsync(run.Id, "HumanFeedbackRequested", $"{node.Label} PR created, awaiting merge", node.Id, runNodeId: runNode.Id);
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
                await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Succeeded));
                var completedOutput = BuildNodeCompletedOutput(execResult);
                await LogEventAsync(run.Id, "NodeCompleted", $"{node.Label} succeeded", node.Id, completedOutput, runNode.Id);
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
            await NotifyAsync(() => _notifier.NodeStateChangedAsync(run.Id, node.Id, LoopRunNodeStatus.Running, LoopRunNodeStatus.Failed));
            await LogEventAsync(run.Id, "NodeFailed", $"{node.Label} failed: {execResult.Error}", node.Id, execResult.Output, runNode.Id);

            if (hasFailureEdge)
                return new RunNodeOutcome(LoopRunNodeStatus.Failed, execResult.Output);

            if (attempt > maxRetries)
                return new RunNodeOutcome(LoopRunNodeStatus.Failed, execResult.Output);
        }
    }

    private async Task<LoopRunNode> CreateRunNodeAsync(Guid runId, Guid nodeId, string label)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();

        var runNode = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            NodeLabel = label,
            Status = LoopRunNodeStatus.Pending,
            RetryCount = 0,
        };
        await loopRunStore.CreateRunNodeAsync(runNode);
        return runNode;
    }

    private async Task PersistEdgeTraversalAsync(Guid runId, Guid edgeId, int count)
    {
        using var scope = _sp.CreateScope();
        var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        await loopRunStore.PersistEdgeTraversalAsync(runId, edgeId, count);
    }

    private async Task<LoopRunStatus> CompleteRunAsync(Guid runId, string? cleanupOutput = null)
    {
        await LogEventAsync(runId, "CleanupStarted", "Cleanup phase started");
        if (!string.IsNullOrEmpty(cleanupOutput))
            await LogEventAsync(runId, "CleanupCompleted", $"Cleanup finished: {cleanupOutput}");

        Guid? workItemId = null;
        using (var scope = _sp.CreateScope())
        {
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await loopRunStore.GetByIdAsync(runId);
            if (run != null)
            {
                workItemId = run.WorkItemId;
                run.Status = LoopRunStatus.Completed;
                run.CompletedAt = DateTime.UtcNow;
                run.UpdatedAt = DateTime.UtcNow;
                await loopRunStore.UpdateRunAsync(run);
            }
        }
        if (workItemId.HasValue)
            await TransitionWorkItemAsync(workItemId.Value, WorkItemStatus.Done, null);
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Completed));
        await LogEventAsync(runId, "LoopRunCompleted", "Run completed successfully");
        ReleaseRunControl(runId);
        return LoopRunStatus.Completed;
    }

    private async Task<LoopRunStatus> FailRunAsync(Guid runId, string reason)
    {
        Guid? workItemId = null;
        using (var scope = _sp.CreateScope())
        {
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await loopRunStore.GetByIdAsync(runId);
            if (run != null)
            {
                workItemId = run.WorkItemId;
                run.Status = LoopRunStatus.Failed;
                run.CompletedAt = DateTime.UtcNow;
                run.UpdatedAt = DateTime.UtcNow;
                await loopRunStore.UpdateRunAsync(run);
            }
        }
        if (workItemId.HasValue)
            await TransitionWorkItemAsync(workItemId.Value, WorkItemStatus.HumanFeedback, reason);
        await NotifyAsync(() => _notifier.EventLoggedAsync(runId, $"Run failed: {reason}", "Error", null, null));
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Failed));
        ReleaseRunControl(runId);
        return LoopRunStatus.Failed;
    }

    private async Task CancelRunInternalAsync(Guid runId)
    {
        Guid? workItemId = null;
        using (var scope = _sp.CreateScope())
        {
            var loopRunStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
            var run = await loopRunStore.GetByIdAsync(runId);
            if (run != null)
            {
                workItemId = run.WorkItemId;
                run.Status = LoopRunStatus.Cancelled;
                run.CompletedAt = DateTime.UtcNow;
                run.UpdatedAt = DateTime.UtcNow;
                await loopRunStore.UpdateRunAsync(run);
            }
        }
        if (workItemId.HasValue)
            await TransitionWorkItemAsync(workItemId.Value, WorkItemStatus.HumanFeedback, "Run cancelled");
        await NotifyAsync(() => _notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Cancelled));
        ReleaseRunControl(runId);
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
                var logger = scope.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<LoopEngine>>();
                logger?.LogWarning("TransitionWorkItemAsync: WorkItem {WorkItemId} not found", workItemId);
                return;
            }
            prev = wi.Status;
            if (prev == next) return;
            wi.Status = next;
            if (reason != null) wi.HumanFeedbackReason = reason;
            wi.UpdatedAt = DateTime.UtcNow;
            await workItemStore.UpdateAsync(wi);
        }
        await NotifyAsync(() => _workItemNotifier.WorkItemStateChangedAsync(workItemId, prev, next));
        if (next == WorkItemStatus.HumanFeedback && reason != null)
            await NotifyAsync(() => _workItemNotifier.HumanFeedbackRequiredAsync(workItemId, reason));
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

        await NotifyAsync(() => _notifier.NodeStateChangedAsync(runId, prRunNode.LoopNodeId, LoopRunNodeStatus.WaitingHuman, prRunNode.Status));
    }
}
