using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Each conversation entry the engine writes names the node execution it came
/// from, so the UI can tie a turn to what that execution did (e.g. the loop
/// variables it wrote) without guessing from labels and timestamps.
/// </summary>
public class LoopEngineConversationLinkTests
{
    [Fact]
    public async Task An_AI_turn_names_the_execution_that_produced_it()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Developer");
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("ai", "c", EdgeType.OnSuccess);
        h.Registry.Register(new ScriptedExecutor(NodeType.AI,
            new NodeOutcome.NodeStarting("ai"),
            new NodeOutcome.Success(EdgeType.OnSuccess, "implemented it")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));
        Guid? linked = null;
        h.WorkItemsMock
            .Setup(m => m.AppendAiTurnAsync(h.WorkItemId, "Developer", "implemented it", It.IsAny<Guid?>()))
            .Callback((string _, string _, string _, Guid? runNodeId) => linked = runNodeId)
            .ReturnsAsync(true);

        h.SeedRun("ai");
        await h.RunAsync();

        var aiExecution = h.ReloadRunNodes().Single(rn => rn.LoopNodeId == h.NodesById["ai"].Id);
        Assert.Equal(aiExecution.Id, linked);
    }

    [Fact]
    public async Task A_park_for_a_human_names_the_execution_that_parked()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("human", NodeType.Human, "Approve");
        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("human"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded)));
        Guid? linked = null;
        h.WorkItemsMock
            .Setup(m => m.TransitionAsync(
                h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<Guid?>()))
            .Callback((string _, RemoteWorkItemStatus _, string? _, string? _, Guid? _, string? _, string? _, Guid? runNodeId)
                => linked = runNodeId)
            .ReturnsAsync(true);

        h.SeedRun("human");
        await h.RunAsync();

        var humanExecution = Assert.Single(h.ReloadRunNodes());
        Assert.Equal(humanExecution.Id, linked);
    }

    [Theory]
    [InlineData(LoopRunNodeStatus.Running, true)]
    // Halt pressed after the node finished but before the next one started: the
    // halt is not that execution's turn, so it must not borrow its variables.
    [InlineData(LoopRunNodeStatus.Succeeded, false)]
    public async Task A_halt_names_the_execution_it_interrupted_and_only_that(
        LoopRunNodeStatus executionStatus, bool expectLinked)
    {
        using var h = new LoopEngineHarness();
        h.AddNode("ai", NodeType.AI, "Developer");
        h.SeedRun("ai");
        var execution = new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = h.RunId,
            LoopNodeId = h.NodesById["ai"].Id,
            NodeLabel = "Developer",
            Status = executionStatus,
            StartedAt = DateTime.UtcNow,
        };
        h.Db.Context.LoopRunNodes.Add(execution);
        h.Db.Context.SaveChanges();
        Guid? linked = Guid.Empty;
        h.WorkItemsMock
            .Setup(m => m.TransitionAsync(
                h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<Guid?>()))
            .Callback((string _, RemoteWorkItemStatus _, string? _, string? _, Guid? _, string? _, string? _, Guid? runNodeId)
                => linked = runNodeId)
            .ReturnsAsync(true);

        await h.Engine.HaltRunAsync(h.RunId);

        Assert.Equal(expectLinked ? execution.Id : null, linked);
    }
}
