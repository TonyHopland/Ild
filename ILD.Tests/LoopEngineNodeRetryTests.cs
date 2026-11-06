using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;

namespace ILD.Tests;

public class LoopEngineNodeRetryTests
{
    [Fact]
    public async Task A_Retrying_outcome_writes_a_NodeRetrying_event_and_the_node_carries_on()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("s", NodeType.Start);
        h.AddNode("pr", NodeType.PR, "Open PR");
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("s", "pr", EdgeType.OnSuccess);
        h.AddEdge("pr", "c", EdgeType.OnSuccess);
        h.Registry.Register(new ScriptedExecutor(NodeType.Start,
            new NodeOutcome.NodeStarting("start"), new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(new ScriptedExecutor(NodeType.PR,
            new NodeOutcome.NodeStarting("pr"),
            new NodeOutcome.Retrying(1, 3, "remote: fatal error in commit_refs_rules", TimeSpan.FromSeconds(15)),
            new NodeOutcome.Success(EdgeType.OnSuccess, "done")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"), new NodeOutcome.Terminal("done")));
        h.SeedRun("s");

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        var retry = Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.NodeRetrying);
        Assert.Contains("retry 1/3 in 15s", retry.Data);
        Assert.Contains("commit_refs_rules", retry.Data);
    }
}
