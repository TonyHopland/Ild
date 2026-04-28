using FluentAssertions;
using ILD.Core.Enums;
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
        h.ReloadWorkItem().Status.Should().Be(WorkItemStatus.HumanFeedback);
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

        h.ReloadWorkItem().Status.Should().Be(WorkItemStatus.HumanFeedback);
        h.ReloadRunNodes().Should().Contain(n => n.Status == LoopRunNodeStatus.WaitingHuman);
    }
}
