using FluentAssertions;
using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

public class LoopEngineTests
{
    [Fact]
    public async Task Happy_path_Start_Cmd_Cleanup_completes_successfully()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        var run = h.ReloadRun();
        run.Status.Should().Be(LoopRunStatus.Completed);

        var runNodes = h.ReloadRunNodes();
        runNodes.Should().HaveCount(3);
        runNodes.Select(n => n.Status).Should().AllBeEquivalentTo(LoopRunNodeStatus.Succeeded);
    }

    [Fact]
    public async Task Cmd_failure_with_no_failure_edge_no_retries_routes_workitem_to_HumanFeedback()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.Save();

        h.Fakes[NodeType.Cmd].Behavior = _ => NodeExecutionResult.Fail("boom");

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Failed);
        ((WorkItemStatus)(int)h.ReloadServerWorkItem().Status).Should().Be(WorkItemStatus.HumanFeedback);
    }

    [Fact]
    public async Task On_failure_edge_routes_to_alternative_node_immediately_no_retry()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("a", NodeType.Cmd),
            ("fix", NodeType.Cmd),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c", EdgeType.OnSuccess);
        h.AddEdge("e3", "a", "fix", EdgeType.OnFailure);
        h.AddEdge("e4", "fix", "c");
        h.Save();

        var attempts = 0;
        h.Fakes[NodeType.Cmd].Behavior = ctx =>
        {
            attempts++;
            return ctx.Node.Label == "a" ? NodeExecutionResult.Fail("boom") : NodeExecutionResult.Ok("fixed");
        };

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        attempts.Should().Be(2); // 'a' failed once + 'fix' succeeded; no retries on 'a'
    }

    [Fact]
    public async Task Auto_retry_succeeds_within_MaxRetries()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.NodesById["a"].MaxRetries = 2;
        h.Save();

        var attempts = 0;
        h.Fakes[NodeType.Cmd].Behavior = _ =>
        {
            attempts++;
            return attempts < 3 ? NodeExecutionResult.Fail("flaky") : NodeExecutionResult.Ok("ok");
        };

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task Cycle_with_MaxTraversals_terminates()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "a", EdgeType.OnSuccess, maxTraversals: 3); // self-loop bounded
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Failed);
        h.Fakes[NodeType.Cmd].InvocationCount.Should().Be(4); // initial + 3 traversals
    }

    [Fact]
    public async Task Cancellation_marks_run_Cancelled()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "a", EdgeType.OnSuccess); // infinite cycle (no max)
        h.Save();

        using var cts = new CancellationTokenSource();
        h.Fakes[NodeType.Cmd].AsyncBehavior = async ctx =>
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(20));
            await Task.Delay(500, ctx.CancellationToken);
            return NodeExecutionResult.Ok();
        };

        await h.Engine.RunAsync(h.RunId, cts.Token);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Cancelled);
    }

    [Fact]
    public async Task Human_node_pauses_run_and_routes_workitem_to_HumanFeedback()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("review", NodeType.Human),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "review");
        h.AddEdge("e2", "review", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        ((WorkItemStatus)(int)h.ReloadServerWorkItem().Status).Should().Be(WorkItemStatus.HumanFeedback);
        h.ReloadRun().HumanFeedbackReason.Should().Be(ILD.Data.Enums.HumanFeedbackReasons.HumanInputNeeded);
        h.ReloadRunNodes().Should().Contain(n => n.Status == LoopRunNodeStatus.WaitingHuman);
    }

    [Fact]
    public async Task PR_node_pauses_run_and_sets_PrUrl_on_workitem()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("pr", NodeType.PR),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "pr");
        h.AddEdge("e2", "pr", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        ((WorkItemStatus)(int)h.ReloadServerWorkItem().Status).Should().Be(WorkItemStatus.HumanFeedback);
        h.ReloadRun().HumanFeedbackReason.Should().Be(ILD.Data.Enums.HumanFeedbackReasons.PrAwaitingMerge);
    }

    [Fact]
    public async Task PR_merge_signal_resumes_run_and_routes_to_on_success()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("pr", NodeType.PR),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "pr");
        h.AddEdge("e2", "pr", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);
        ((WorkItemStatus)(int)h.ReloadServerWorkItem().Status).Should().Be(WorkItemStatus.HumanFeedback);

        var prNodeId = h.NodesById["pr"].Id;
        var prRunNode = h.ReloadRunNodes().First(n => n.LoopNodeId == prNodeId);
        await h.Engine.SignalNodeResultAsync(h.RunId, prRunNode.Id, NodeSignal.Succeeded());
        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        var cleanupNodeId = h.NodesById["c"].Id;
        h.ReloadRunNodes().Should().Contain(n => n.LoopNodeId == cleanupNodeId && n.Status == LoopRunNodeStatus.Succeeded);
    }

    [Fact]
    public async Task PR_rejection_signal_resumes_run_and_routes_to_on_failure()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("pr", NodeType.PR),
            ("fix", NodeType.Cmd),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "pr");
        h.AddEdge("e2", "pr", "c", EdgeType.OnSuccess);
        h.AddEdge("e3", "pr", "fix", EdgeType.OnFailure);
        h.AddEdge("e4", "fix", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);
        ((WorkItemStatus)(int)h.ReloadServerWorkItem().Status).Should().Be(WorkItemStatus.HumanFeedback);

        var prNodeId = h.NodesById["pr"].Id;
        var prRunNode = h.ReloadRunNodes().First(n => n.LoopNodeId == prNodeId);
        await h.Engine.SignalNodeResultAsync(h.RunId, prRunNode.Id, NodeSignal.Failed("PR rejected"));
        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        var fixNodeId = h.NodesById["fix"].Id;
        h.ReloadRunNodes().Should().Contain(n => n.LoopNodeId == fixNodeId && n.Status == LoopRunNodeStatus.Succeeded);
    }

    [Fact]
    public async Task NodeStarted_event_contains_structured_effective_input()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.NodesById["a"].Config = "{\"command\": \"echo hello\"}";
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        var eventLogs = h.Db.Fresh().EventLogs.Where(e => e.LoopRunId == h.RunId).ToList();
        var cmdStarted = eventLogs.FirstOrDefault(e => e.EventType == EventType.NodeStarted && e.NodeId == h.NodesById["a"].Id);
        cmdStarted.Should().NotBeNull("Cmd node should have NodeStarted event");
        cmdStarted!.Data.Should().Contain("nodeType");
        cmdStarted.Data.Should().Contain("Cmd");
        cmdStarted.Data.Should().Contain("echo hello");
    }

    [Fact]
    public async Task Multiple_visits_to_same_template_node_create_separate_LoopRunNode_rows()
    {
        using var h = new EngineHarness();
        // Graph: Start → A → B → A → B (edge limit) → fail
        // A has only one OnSuccess edge (→B), B has only one OnSuccess edge (→A, bounded)
        // A executes 2 times, B executes 2 times
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("a", NodeType.Cmd),
            ("b", NodeType.Cmd));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "b");
        h.AddEdge("e3", "b", "a", EdgeType.OnSuccess, maxTraversals: 2); // loop back twice
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Failed); // edge exceeded max traversals
        var aId = h.NodesById["a"].Id;
        var runNodesForA = h.ReloadRunNodes().Where(rn => rn.LoopNodeId == aId).ToList();

        // Each visit to node A creates its own LoopRunNode row (at least 2, proving per-execution model)
        runNodesForA.Count.Should().BeGreaterThanOrEqualTo(2);
        runNodesForA.Should().AllSatisfy(n => n.Status.Should().Be(LoopRunNodeStatus.Succeeded));
    }

    [Fact]
    public async Task EventLog_entries_reference_specific_execution_via_RunNodeId()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        var runNodes = h.ReloadRunNodes();
        runNodes.Should().HaveCount(3); // Start, Cmd, Cleanup

        // Event log entries for node executions should reference the specific LoopRunNode
        var eventLogs = h.Db.Fresh().EventLogs.Where(e => e.LoopRunId == h.RunId).ToList();
        var nodeEvents = eventLogs.Where(e => e.EventType is EventType.NodeStarted or EventType.NodeCompleted).ToList();
        nodeEvents.Should().NotBeEmpty();
        nodeEvents.Should().AllSatisfy(e =>
        {
            e.RunNodeId.Should().NotBeNull("event should reference a specific execution");
            runNodes.Should().Contain(rn => rn.Id == e.RunNodeId);
        });
    }

    [Fact]
    public async Task GetRunNodeAsync_returns_latest_execution_for_node()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "a", EdgeType.OnSuccess, maxTraversals: 1); // A visited 2x then fails on edge limit
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        // GetRunNodeAsync should return the latest execution, not the first
        var latest = await h.Db.LoopRuns.GetRunNodeAsync(h.RunId, h.NodesById["a"].Id);
        latest.Should().NotBeNull();
        // There are 2 rows for node A; GetRunNodeAsync returns the most recent one
        var allForA = h.ReloadRunNodes().Where(rn => rn.LoopNodeId == h.NodesById["a"].Id).ToList();
        allForA.Should().HaveCount(2);
        latest.Id.Should().Be(allForA[1].Id); // latest is the second one
    }

    [Fact]
    public async Task GetRunStatusAsync_returns_null_when_run_does_not_exist()
    {
        using var h = new EngineHarness();
        var status = await h.Engine.GetRunStatusAsync(Guid.NewGuid());
        status.Should().BeNull();
    }

    [Fact]
    public async Task GetRunStatusAsync_returns_status_when_run_exists()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "c");
        h.Save();

        var status = await h.Engine.GetRunStatusAsync(h.RunId);
        status.Should().Be(LoopRunStatus.Running);
    }

    [Fact]
    public async Task RunAsync_with_WaitingHuman_CurrentNodeId_returns_early_without_reexecuting_nodes()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("review", NodeType.Human),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "review");
        h.AddEdge("e2", "review", "c");
        h.Save();

        // First run pauses at the human node.
        await h.Engine.RunAsync(h.RunId);
        ((WorkItemStatus)(int)h.ReloadServerWorkItem().Status).Should().Be(WorkItemStatus.HumanFeedback);
        var startInvocations = h.Fakes[NodeType.Start].InvocationCount;

        // Simulate server restart: RunAsync is called again.
        // The run's CurrentNodeId points to the Human node in WaitingHuman state.
        await h.Engine.RunAsync(h.RunId);

        // Should return early without re-executing any nodes.
        h.Fakes[NodeType.Start].InvocationCount.Should().Be(startInvocations);
        h.ReloadRun().Status.Should().Be(LoopRunStatus.WaitingHuman);
        ((WorkItemStatus)(int)h.ReloadServerWorkItem().Status).Should().Be(WorkItemStatus.HumanFeedback);
    }

    [Fact]
    public async Task Human_node_resume_with_succeeded_run_node_routes_on_success_and_passes_input_as_PreviousNode_Output()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("ask", NodeType.Human),
            ("after", NodeType.Cmd),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "ask");
        h.AddEdge("e2", "ask", "after", EdgeType.OnSuccess);
        h.AddEdge("e3", "after", "c");
        h.Save();

        // First run pauses at the human node.
        await h.Engine.RunAsync(h.RunId);
        var humanRunNode = h.ReloadRunNodes().First(n => n.LoopNodeId == h.NodesById["ask"].Id);
        humanRunNode.Status.Should().Be(ILD.Data.Enums.LoopRunNodeStatus.WaitingHuman);

        // Simulate the manager finalizing the run node with the human's input as output.
        // Update through the tracked context so the singleton store sees it.
        var trackedRn = h.Db.Context.LoopRunNodes.First(n => n.Id == humanRunNode.Id);
        trackedRn.Status = LoopRunNodeStatus.Succeeded;
        trackedRn.Output = "go ahead";
        trackedRn.CompletedAt = DateTime.UtcNow;
        h.Db.Context.SaveChanges();

        string? observedPrevious = null;
        h.Fakes[NodeType.Cmd].Behavior = ctx =>
        {
            observedPrevious = ctx.PreviousNodeOutput;
            return NodeExecutionResult.Ok("done");
        };

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        observedPrevious.Should().Be("go ahead");
    }

    [Fact]
    public async Task Human_node_resume_with_failed_run_node_routes_on_failure()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("ask", NodeType.Human),
            ("fix", NodeType.Cmd),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "ask");
        h.AddEdge("e2", "ask", "c", EdgeType.OnSuccess);
        h.AddEdge("e3", "ask", "fix", EdgeType.OnFailure);
        h.AddEdge("e4", "fix", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);
        var humanRunNode = h.ReloadRunNodes().First(n => n.LoopNodeId == h.NodesById["ask"].Id);

        var trackedRn = h.Db.Context.LoopRunNodes.First(n => n.Id == humanRunNode.Id);
        trackedRn.Status = LoopRunNodeStatus.Failed;
        trackedRn.CompletedAt = DateTime.UtcNow;
        h.Db.Context.SaveChanges();

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        var fixNodeId = h.NodesById["fix"].Id;
        h.ReloadRunNodes().Should().Contain(n => n.LoopNodeId == fixNodeId && n.Status == LoopRunNodeStatus.Succeeded);
    }

    [Fact]
    public async Task Cleanup_node_emits_CleanupStarted_and_CleanupCompleted_events_before_run_completion()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.Save();

        await h.Engine.RunAsync(h.RunId);

        var logs = h.ReloadEventLogs();
        var cleanupStarted = logs.FirstOrDefault(e => e.EventType == EventType.CleanupStarted);
        var cleanupCompleted = logs.FirstOrDefault(e => e.EventType == EventType.CleanupCompleted);
        var runCompleted = logs.FirstOrDefault(e => e.EventType == EventType.LoopRunCompleted);

        cleanupStarted.Should().NotBeNull("CleanupStarted event should be logged");
        cleanupCompleted.Should().NotBeNull("CleanupCompleted event should be logged");
        runCompleted.Should().NotBeNull("LoopRunCompleted event should be logged");

        cleanupStarted!.Sequence.Should().BeLessThan(cleanupCompleted!.Sequence, "CleanupStarted should precede CleanupCompleted");
        cleanupCompleted.Sequence.Should().BeLessThan(runCompleted!.Sequence, "CleanupCompleted should precede LoopRunCompleted");
    }

    [Fact]
    public async Task Human_node_resume_with_responded_run_node_routes_on_respond()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("ask", NodeType.Human),
            ("iterate", NodeType.AI),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "ask");
        h.AddEdge("e2", "ask", "iterate", EdgeType.OnRespond);
        h.AddEdge("e3", "ask", "c", EdgeType.OnSuccess);
        h.AddEdge("e4", "iterate", "c");
        h.Save();

        // First run pauses at the human node.
        await h.Engine.RunAsync(h.RunId);
        var humanRunNode = h.ReloadRunNodes().First(n => n.LoopNodeId == h.NodesById["ask"].Id);
        humanRunNode.Status.Should().Be(LoopRunNodeStatus.WaitingHuman);

        // Simulate the manager finalizing the run node with Responded status.
        var trackedRn = h.Db.Context.LoopRunNodes.First(n => n.Id == humanRunNode.Id);
        trackedRn.Status = LoopRunNodeStatus.Responded;
        trackedRn.Output = "please revise";
        trackedRn.CompletedAt = DateTime.UtcNow;
        h.Db.Context.SaveChanges();

        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        var iterateNodeId = h.NodesById["iterate"].Id;
        h.ReloadRunNodes().Should().Contain(n => n.LoopNodeId == iterateNodeId);
    }

    [Fact]
    public async Task SignalNodeResultAsync_resumes_run_and_executes_cleanup_node()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(
            ("s", NodeType.Start),
            ("pr", NodeType.PR),
            ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "pr");
        h.AddEdge("e2", "pr", "c");
        h.Save();

        // Run until PR node enters WaitingHuman
        await h.Engine.RunAsync(h.RunId);

        h.ReloadRun().Status.Should().Be(LoopRunStatus.WaitingHuman);
        var prRunNode = h.ReloadRunNodes().First(n => n.LoopNodeId == h.NodesById["pr"].Id);
        prRunNode.Status.Should().Be(LoopRunNodeStatus.WaitingHuman);

        // Signal PR as merged (simulates mark-merged endpoint)
        await h.Engine.SignalNodeResultAsync(h.RunId, prRunNode.Id, NodeSignal.Succeeded());

        // Resume the engine
        await h.Engine.RunAsync(h.RunId);

        // Verify run completed with cleanup node
        h.ReloadRun().Status.Should().Be(LoopRunStatus.Completed);
        var nodes = h.ReloadRunNodes();
        var cleanupNode = nodes.FirstOrDefault(n => n.LoopNodeId == h.NodesById["c"].Id);
        cleanupNode.Should().NotBeNull("Cleanup node should have been executed after PR merge");
        cleanupNode!.Status.Should().Be(LoopRunNodeStatus.Succeeded);
    }
}
