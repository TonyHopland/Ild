using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;

namespace ILD.Tests;

public class EngineNodeStartingContractTests
{
    [Fact]
    public async Task WaitingAction_without_NodeStarting_does_not_create_LoopRunNode()
    {
        // The engine only creates a LoopRunNode row when NodeStarting is yielded.
        // If an executor yields WaitingAction directly, no row should be persisted.
        // This test verifies the contract: WaitingAction without NodeStarting
        // produces zero LoopRunNode rows.
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);

        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.WaitingAction("Awaiting input", "prompt")));

        h.SeedRun("h");
        await h.RunAsync();

        // No LoopRunNode should exist because NodeStarting was never yielded.
        Assert.Empty(h.ReloadRunNodes());
    }

    [Fact]
    public async Task WaitingAction_with_NodeStarting_creates_LoopRunNode_with_WaitingHuman_status()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("h", NodeType.Human);

        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction("Awaiting input", "prompt")));

        h.SeedRun("h");
        await h.RunAsync();

        var nodes = h.ReloadRunNodes();
        Assert.Single(nodes);
        Assert.Equal(LoopRunNodeStatus.WaitingHuman, nodes[0].Status);
        Assert.Equal("ask", nodes[0].EffectiveInput);
    }

    [Fact]
    public async Task Fail_without_NodeStarting_does_not_create_LoopRunNode()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);

        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.Fail(EdgeType.OnFailure, "precondition failed")));

        h.SeedRun("a");
        await h.RunAsync();

        // No LoopRunNode should exist because NodeStarting was never yielded.
        Assert.Empty(h.ReloadRunNodes());

        // The run should fail because there's no OnFailure edge.
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task Success_without_NodeStarting_does_not_create_LoopRunNode()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.Cmd);
        h.AddNode("b", NodeType.Cmd);
        h.AddEdge("a", "b", EdgeType.OnSuccess);

        h.Registry.Register(new ScriptedExecutor(NodeType.Cmd,
            new NodeOutcome.Success(EdgeType.OnSuccess, "a-output"))
            .Then(
                new NodeOutcome.NodeStarting("b"),
                new NodeOutcome.Terminal("done")));

        h.SeedRun("a");
        await h.RunAsync();

        // Only node b's run-node exists; node a skipped NodeStarting.
        var nodes = h.ReloadRunNodes();
        Assert.Single(nodes);
        Assert.Equal("b", nodes[0].NodeLabel);
    }
}
