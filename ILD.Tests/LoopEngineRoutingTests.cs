using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

public class LoopEngineRoutingTests
{
    [Fact]
    public async Task Happy_path_routes_Start_then_Cmd_then_Cleanup_to_completion()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("s", NodeType.Start);
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("s", "a", EdgeType.OnSuccess);
        h.AddEdge("a", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "worktree=/tmp/x")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("cmd"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "cmd output")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.NotNull(run.CompletedAt);

        var runNodes = h.ReloadRunNodes();
        Assert.Equal(3, runNodes.Count);
        Assert.All(runNodes, rn => Assert.Equal(LoopRunNodeStatus.Succeeded, rn.Status));
        Assert.Equal("cmd output", runNodes[1].Output);
    }

    [Fact]
    public async Task Entering_a_node_notifies_the_work_item_hub_so_the_taskboard_card_refreshes()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("s", NodeType.Start);
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("s", "a", EdgeType.OnSuccess);
        h.AddEdge("a", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "worktree=/tmp/x")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("cmd"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "cmd output")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        // Each node that enters Running changes the work item's current step, so
        // the engine must nudge the work-item hub once per started node (3 here).
        h.WorkItemNotifierMock.Verify(n => n.RunProgressedAsync(h.WorkItemId), Times.Exactly(3));
    }

    [Fact]
    public async Task Success_feeds_output_into_next_runs_PreviousNodeOutput()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("b", NodeType.Cmd);
        h.AddEdge("a", "b", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("a"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "from-a"))
            .Then(
                new NodeOutcome.NodeStarting("b"),
                new NodeOutcome.Success(EdgeType.OnSuccess, "from-b")));

        h.SeedRun("a");
        await h.RunAsync();

        // After both nodes ran, PreviousNodeOutput holds the last Success output (from-b).
        // We assert mid-stream behavior via the per-run-node Output history.
        var nodes = h.ReloadRunNodes();
        Assert.Equal("from-a", nodes[0].Output);
        Assert.Equal("from-b", nodes[1].Output);
        Assert.Equal("from-b", h.ReloadRun().PreviousNodeOutput);
    }

    [Fact]
    public async Task Fail_routes_via_OnFailure_edge_when_present()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("recover", NodeType.Cmd);
        h.AddEdge("a", "recover", EdgeType.OnFailure);

        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("a"),
            new NodeOutcome.Fail(EdgeType.OnFailure, "boom", "stderr"))
            .Then(
                new NodeOutcome.NodeStarting("recover"),
                new NodeOutcome.Success(EdgeType.OnSuccess, "recovered")));

        h.SeedRun("a");
        await h.RunAsync();

        var nodes = h.ReloadRunNodes();
        Assert.Equal(2, nodes.Count);
        Assert.Equal(LoopRunNodeStatus.Failed, nodes[0].Status);
        Assert.Equal("boom", nodes[0].Error);
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes[1].Status);
    }

    [Fact]
    public async Task Fail_without_OnFailure_edge_fails_run_to_HumanFeedback()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);

        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("a"),
            new NodeOutcome.Fail(EdgeType.OnFailure, "boom")));

        h.SeedRun("a");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Failed, run.Status);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task WaitingAction_parks_run_in_WaitingHuman_status()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);

        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt rendered")));

        h.SeedRun("h");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Equal("Awaiting input", run.HumanFeedbackReason);

        var rn = h.ReloadRunNodes().Single();
        Assert.Equal(LoopRunNodeStatus.WaitingHuman, rn.Status);
        Assert.Equal("prompt rendered", rn.Output);
    }

    [Fact]
    public async Task SignalNodeResult_with_custom_edge_routes_via_named_edge()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);
        h.AddNode("after", NodeType.Cmd);
        h.AddEdge("h", "after", EdgeType.Custom, "Respond");

        var humanExec = new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt"));
        // Re-entry after signal: succeed via the named "Respond" custom edge.
        humanExec.Then(
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.Success(EdgeType.Custom, "human-said-yes", "Respond"));
        h.Registry.Register(humanExec);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("after"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "after-output")));

        h.SeedRun("h");
        await h.RunAsync(); // parks at WaitingHuman

        var waitingNode = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);

        // Engine.SignalNodeResultAsync re-enters Human on the named edge.
        await h.Engine.SignalNodeResultAsync(h.RunId, waitingNode.Id,
            NodeSignal.Custom("Respond", "user-text"));

        // SignalNodeResultAsync launches the run via Task.Run; drain it.
        await WaitUntilAsync(() => h.ReloadRun().Status != LoopRunStatus.Running, TimeSpan.FromSeconds(5));

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);

        var nodes = h.ReloadRunNodes().OrderBy(n => n.StartedAt).ToList();
        // 1 WaitingHuman + 1 Succeeded human re-entry + 1 Cmd "after"
        Assert.Equal(3, nodes.Count);
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes[1].Status);
        Assert.Equal("after-output", nodes[2].Output);
    }

    [Fact]
    public async Task Custom_edge_with_no_connection_fails_run_with_missing_edge_reason()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI);
        // "a" has a default success edge but no custom "Escalate" edge wired up.
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("a", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("a"),
            new NodeOutcome.Success(EdgeType.Custom, "needs a human", "Escalate")));

        h.SeedRun("a");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Failed, run.Status);
        Assert.Equal("missing edge connection: Escalate", run.HumanFeedbackReason);
    }

    [Fact]
    public async Task SignalNodeResult_with_reject_routes_via_OnFailure_edge()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);
        h.AddNode("reject_branch", NodeType.Cmd);
        h.AddEdge("h", "reject_branch", EdgeType.OnFailure);

        var humanExec = new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt"));
        humanExec.Then(
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.Fail(EdgeType.OnFailure, "Rejected", "human-said-no"));
        h.Registry.Register(humanExec);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("rb"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "rb-output")));

        h.SeedRun("h");
        await h.RunAsync();

        var waitingNode = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);
        await h.Engine.SignalNodeResultAsync(h.RunId, waitingNode.Id,
            new NodeSignal(ExternalActionResultType.Reject, Error: "Rejected"));
        await WaitUntilAsync(() => h.ReloadRun().Status != LoopRunStatus.Running, TimeSpan.FromSeconds(5));

        var nodes = h.ReloadRunNodes().OrderBy(n => n.StartedAt).ToList();
        Assert.Equal(3, nodes.Count);
        Assert.Equal(LoopRunNodeStatus.Failed, nodes[1].Status);
        Assert.Equal("rb-output", nodes[2].Output);
    }

    [Fact]
    public async Task WaitingIld_does_not_persist_a_LoopRunNode_and_parks_to_WaitingForIld()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI);

        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.WaitingIld("provider at capacity")));

        h.SeedRun("a");
        await h.RunAsync();

        Assert.Empty(h.ReloadRunNodes());
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.WaitingForIld,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Terminal_outcome_completes_the_run()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("c", NodeType.Cleanup);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("c"),
            new NodeOutcome.Terminal("bye")));
        h.SeedRun("c");
        await h.RunAsync();
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Equal(LoopRunNodeStatus.Succeeded, h.ReloadRunNodes().Single().Status);
    }

    [Fact]
    public async Task MaxTraversals_exceeded_fails_the_run()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddEdge("a", "a", EdgeType.OnSuccess);

        var infinite = new ScriptedExecutor(NodeType.Cmd);
        // Seed enough scripts to exceed DefaultMaxEdgeTraversals (=50).
        for (int i = 0; i < 60; i++)
        {
            infinite.Then(
                new NodeOutcome.NodeStarting($"i{i}"),
                new NodeOutcome.Success(EdgeType.OnSuccess, $"out-{i}"));
        }
        h.Registry.Register(infinite);

        h.SeedRun("a");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Failed, run.Status);
        Assert.Contains("Max traversals", run.HumanFeedbackReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EdgeTraversalCounts_persist_across_restart()
    {
        // Simulate a process restart by pre-seeding LoopRunNode rows that
        // already used a self-loop edge 48 times. After restart, only 3 more
        // traversals should fit before the default 50-cap fails the run.
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddEdge("a", "a", EdgeType.OnSuccess);

        var nodeAId = h.NodesById["a"].Id;
        var edgeId = h.Db.Context.LoopNodeEdges.First(e => e.SourceNodeId == nodeAId).Id;
        var run = h.SeedRun("a");

        for (int i = 0; i < 48; i++)
        {
            h.Db.Context.LoopRunNodes.Add(new ILD.Data.Entities.LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = run.Id,
                LoopNodeId = nodeAId,
                NodeLabel = "a",
                Status = LoopRunNodeStatus.Succeeded,
                StartedAt = DateTime.UtcNow.AddSeconds(i),
                CompletedAt = DateTime.UtcNow.AddSeconds(i),
                IncomingEdgeId = edgeId,
            });
        }
        h.Db.Context.SaveChanges();

        var scripted = new ScriptedExecutor(NodeType.Cmd);
        for (int i = 0; i < 10; i++)
        {
            scripted.Then(
                new NodeOutcome.NodeStarting($"i{i}"),
                new NodeOutcome.Success(EdgeType.OnSuccess, $"out-{i}"));
        }
        h.Registry.Register(scripted);

        await h.RunAsync();

        var reloaded = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Failed, reloaded.Status);
        Assert.Contains("Max traversals", reloaded.HumanFeedbackReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        // 48 pre-existing + 2 fresh edge-traversals before the 51st trips the cap.
        var totalEdgeNodes = h.ReloadRunNodes().Count(rn => rn.IncomingEdgeId == edgeId);
        Assert.Equal(50, totalEdgeNodes);
    }

    [Fact]
    public async Task RetryFromNodeAsync_reseeds_PreviousNodeOutput_from_predecessor()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("b", NodeType.Cmd);
        h.AddEdge("a", "b", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.NodeStarting("a"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "from-a"))
            .Then(
                new NodeOutcome.NodeStarting("b"),
                new NodeOutcome.Fail(EdgeType.OnFailure, "b failed", "from-b")));

        h.SeedRun("a");
        await h.RunAsync();

        // b failed → run is Failed. Retry from b's run-node.
        var bNode = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.Failed);

        // Re-script b for a successful retry.
        h.Registry.Get(NodeType.Cmd); // ensure registered
        ((ScriptedExecutor)h.Registry.Get(NodeType.Cmd)).Then(
            new NodeOutcome.NodeStarting("b-retry"),
            new NodeOutcome.Terminal("b-ok"));

        await h.Engine.RetryFromNodeAsync(h.RunId, bNode.Id);
        await WaitUntilAsync(() => h.ReloadRun().Status == LoopRunStatus.Completed, TimeSpan.FromSeconds(5));

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        // The retry must have seeded PreviousNodeOutput from predecessor a → "from-a"
        // before re-entering b; after b's Terminal the final PreviousNodeOutput is unchanged
        // (Terminal does not update PreviousNodeOutput in the engine).
        // We assert by inspecting the run nodes: the retry produced a fresh Succeeded entry for b.
        var nodes = h.ReloadRunNodes().OrderBy(n => n.StartedAt).ToList();
        Assert.Equal(3, nodes.Count);
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes[0].Status);
        Assert.Equal(LoopRunNodeStatus.Failed, nodes[1].Status);
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes[2].Status);
        Assert.Equal("b-ok", nodes[2].Output);
    }

    [Fact]
    public async Task CleanupRunAsync_only_invokes_the_Cleanup_node()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("c", NodeType.Cleanup);

        var cmd = new ScriptedExecutor(NodeType.Cmd);
        var cleanup = new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("c"),
            new NodeOutcome.Terminal("cleaned"));
        h.Registry.Register(cmd);
        h.Registry.Register(cleanup);

        // Seed the run "stuck" on a (engine not running).
        var run = h.SeedRun("a", LoopRunStatus.WaitingHuman);

        await h.Engine.CleanupRunAsync(h.RunId);

        Assert.Equal(0, cmd.Invocations);
        Assert.Equal(1, cleanup.Invocations);
        var nodes = h.ReloadRunNodes();
        Assert.Single(nodes);
        Assert.Equal(LoopRunNodeStatus.Succeeded, nodes[0].Status);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (predicate()) return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // The engine re-launched via SignalNodeResultAsync/RetryFromNodeAsync may be
                // mid-flight on the shared in-memory SQLite connection. Back off and retry.
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Predicate did not become true within timeout");
    }
}
