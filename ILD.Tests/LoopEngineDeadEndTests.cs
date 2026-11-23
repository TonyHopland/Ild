using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Only a Cleanup node completes a run. A node that finishes with a result no
/// edge leaves from is a dead end: the run parks, and the item does not go to Done.
/// </summary>
public class LoopEngineDeadEndTests
{
    private static void AssertParkedAtDeadEnd(LoopEngineHarness h, string node, string reason)
    {
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.False(run.IsHalted);
        Assert.Equal(HumanFeedbackReasons.NodeFailed, run.HumanFeedbackReason);
        Assert.Equal(h.NodesById[node].Id, run.CurrentNodeId);

        var parked = Assert.Single(RunTimeline.Events(h.Db, h.RunId, EventType.RunParked));
        Assert.Equal(reason, parked.Data);
        Assert.Equal(h.NodesById[node].Id, parked.NodeId);

        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.IsAny<string?>(), It.IsAny<string?>(), h.RunId, HumanFeedbackReasons.NodeFailed), Times.Once);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.Done,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }

    private static async Task<LoopEngineHarness> ParkedAfterAsync(NodeOutcome outcome)
    {
        var h = new LoopEngineHarness();
        h.AddNode("pr", NodeType.AI, "Pull Request");
        h.Registry.Register(new ScriptedExecutor(NodeType.AI, new NodeOutcome.NodeStarting("work"), outcome));
        h.SeedRun("pr");
        await h.RunAsync();
        return h;
    }

    [Fact]
    public async Task A_node_succeeding_with_no_OnSuccess_edge_parks_instead_of_completing()
    {
        using var h = await ParkedAfterAsync(new NodeOutcome.Success(EdgeType.OnSuccess, "ok"));

        AssertParkedAtDeadEnd(h, "pr", "Dead end: \"Pull Request\" finished with OnSuccess, which has no outgoing edge");
    }

    [Fact]
    public async Task A_run_reaching_Cleanup_still_completes()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI);
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("a", "c", EdgeType.OnSuccess);
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("work"), new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"), new NodeOutcome.Terminal("done")));
        h.SeedRun("a");

        await h.RunAsync();

        Assert.Equal(LoopRunStatus.Completed, h.ReloadRun().Status);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.Done,
            It.IsAny<string?>(), It.IsAny<string?>(), h.RunId, It.IsAny<string?>()), Times.Once);
    }
}
