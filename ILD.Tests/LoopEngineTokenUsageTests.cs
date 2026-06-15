using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopEngineTokenUsageTests
{
    [Fact]
    public async Task Persists_token_usage_from_a_successful_node_onto_the_run_node()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("s", NodeType.Start);
        h.AddNode("a", NodeType.AI);
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("s", "a", EdgeType.OnSuccess);
        h.AddEdge("a", "c", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "worktree=/tmp/x")));
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("ai"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "ai output", Usage: new TokenUsage(120, 45, 0.0150m))));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        var aiNode = h.ReloadRunNodes().Single(rn => rn.LoopNodeId == h.NodesById["a"].Id);
        Assert.Equal(120, aiNode.InputTokens);
        Assert.Equal(45, aiNode.OutputTokens);
        Assert.Equal(0.0150m, aiNode.CostUsd);
    }

    [Fact]
    public async Task Leaves_token_columns_null_when_a_node_reports_no_usage()
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

        var cmdNode = h.ReloadRunNodes().Single(rn => rn.LoopNodeId == h.NodesById["a"].Id);
        Assert.Null(cmdNode.InputTokens);
        Assert.Null(cmdNode.OutputTokens);
        Assert.Null(cmdNode.CostUsd);
    }
}
