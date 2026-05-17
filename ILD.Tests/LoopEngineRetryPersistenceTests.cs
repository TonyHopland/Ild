using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopEngineRetryPersistenceTests
{
    [Fact]
    public async Task Auto_retry_persists_single_LoopRunNode_per_node_with_retry_count()
    {
        using var h = new EngineHarness();
        h.BuildSimpleGraph(("s", NodeType.Start), ("a", NodeType.Cmd), ("c", NodeType.Cleanup));
        h.AddEdge("e1", "s", "a");
        h.AddEdge("e2", "a", "c");
        h.NodesById["a"].MaxRetries = 2;
        h.Save();

        h.Fakes[NodeType.Cmd].Behavior = _ => NodeExecutionResult.Fail("boom");

        await h.Engine.RunAsync(h.RunId);

        var aId = h.NodesById["a"].Id;
        var runNodesForA = h.ReloadRunNodes().Where(rn => rn.LoopNodeId == aId).ToList();

        // PRD #003 Bug B: one row per node, not one per attempt
        Assert.Single(runNodesForA);
        Assert.Equal(2, runNodesForA[0].RetryCount);
        Assert.Equal(LoopRunNodeStatus.Failed, runNodesForA[0].Status);
    }

    [Fact]
    public async Task Failure_edge_is_followed_on_first_failure_without_retry()
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
        h.NodesById["a"].MaxRetries = 5; // would loop many times if retry kicked in
        h.Save();

        var aAttempts = 0;
        h.Fakes[NodeType.Cmd].Behavior = ctx =>
        {
            if (ctx.Node.Label == "a")
            {
                aAttempts++;
                return NodeExecutionResult.Fail("boom");
            }
            return NodeExecutionResult.Ok("fixed");
        };

        await h.Engine.RunAsync(h.RunId);

        // PRD #003 Bug A: with on_failure edge, no retry on first failure
        Assert.Equal(1, aAttempts);

        var aId = h.NodesById["a"].Id;
        var runNodesForA = h.ReloadRunNodes().Where(rn => rn.LoopNodeId == aId).ToList();
        Assert.Single(runNodesForA);
        Assert.Equal(0, runNodesForA[0].RetryCount);
    }
}
