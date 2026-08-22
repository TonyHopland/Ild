using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The global runaway-graph safety net (ADR-0018): a run may execute at most
/// <c>ai.maxTraversals</c> AI nodes between human interactions, and reaching the
/// cap parks it for a person rather than failing it.
/// </summary>
public class LoopEngineAiTraversalCapTests
{
    private static async Task SetCapAsync(LoopEngineHarness h, int cap)
        => await h.Db.Settings.UpsertAsync(AppSettingKeys.MaxAiTraversals, cap.ToString());

    private static ScriptedExecutor AiRunning(int times)
    {
        var ai = new ScriptedExecutor(NodeType.AI);
        for (var i = 0; i < times; i++)
            ai.Then(new NodeOutcome.NodeStarting($"ai-{i}"), new NodeOutcome.Success(EdgeType.OnSuccess, $"out-{i}"));
        return ai;
    }

    [Fact]
    public async Task Only_AI_nodes_spend_the_budget()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("s", NodeType.Start);
        h.AddNode("p", NodeType.Prompt);
        h.AddNode("a1", NodeType.AI);
        h.AddNode("cmd", NodeType.Cmd);
        h.AddNode("a2", NodeType.AI);
        h.AddNode("c", NodeType.Cleanup);
        h.AddEdge("s", "p", EdgeType.OnSuccess);
        h.AddEdge("p", "a1", EdgeType.OnSuccess);
        h.AddEdge("a1", "cmd", EdgeType.OnSuccess);
        h.AddEdge("cmd", "a2", EdgeType.OnSuccess);
        h.AddEdge("a2", "c", EdgeType.OnSuccess);

        foreach (var type in new[] { NodeType.Start, NodeType.Prompt, NodeType.Cmd })
            h.Registry.Register(new ScriptedExecutor(type,
                new NodeOutcome.NodeStarting(type.ToString()),
                new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(AiRunning(2));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"),
            new NodeOutcome.Terminal("done")));

        h.SeedRun("s");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        // Six nodes ran; only the two AI ones are charged.
        Assert.Equal(6, run.NodeExecutionCount);
        Assert.Equal(2, run.AiTraversalCount);
    }

    [Fact]
    public async Task Parking_for_a_human_refills_the_budget()
    {
        using var h = new LoopEngineHarness();
        await SetCapAsync(h, 5);
        h.AddNode("a", NodeType.AI);
        h.AddNode("human", NodeType.Human);
        h.AddEdge("a", "human", EdgeType.OnSuccess);

        h.Registry.Register(AiRunning(1));
        h.Registry.Register(new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask"),
            new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, "prompt")));

        h.SeedRun("a");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.False(run.IsHalted);
        Assert.Equal(0, run.AiTraversalCount);
    }

    /// <summary>
    /// The symptom the cap exists to not cause: a grill-me style AI ↔ Human
    /// conversation runs far more AI nodes than the cap over its lifetime and
    /// must never trip it, because a human answers between every one.
    /// </summary>
    [Fact]
    public async Task A_long_human_AI_conversation_never_trips_the_cap()
    {
        using var h = new LoopEngineHarness();
        await SetCapAsync(h, 2);
        h.AddNode("a", NodeType.AI);
        h.AddNode("human", NodeType.Human);
        h.AddEdge("a", "human", EdgeType.OnSuccess);
        h.AddEdge("human", "a", EdgeType.Custom, "Respond");

        const int turns = 6;
        h.Registry.Register(AiRunning(turns));
        var human = new ScriptedExecutor(NodeType.Human);
        for (var i = 0; i < turns; i++)
        {
            human.Then(new NodeOutcome.NodeStarting($"ask-{i}"),
                new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, "prompt"));
            human.Then(new NodeOutcome.Success(EdgeType.Custom, $"answer-{i}", "Respond"));
        }
        h.Registry.Register(human);

        h.SeedRun("a");
        await h.RunAsync();

        for (var i = 1; i < turns; i++)
        {
            var waiting = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);
            await h.Engine.SignalNodeResultAsync(h.RunId, waiting.Id, NodeSignal.Custom("Respond", "go on"));
            await h.WaitUntilIdleAsync();
        }

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.False(run.IsHalted);
        Assert.Null(run.HaltReason);
        // Six AI nodes ran under a cap of two, because the budget refills every turn.
        Assert.Equal(turns, h.ReloadRunNodes().Count(rn => rn.NodeLabel == "a"));
    }

    [Fact]
    public async Task Reaching_the_cap_parks_the_run_for_a_human_and_does_not_fail_it()
    {
        using var h = new LoopEngineHarness();
        await SetCapAsync(h, 3);
        h.AddNode("a", NodeType.AI);
        h.AddEdge("a", "a", EdgeType.OnSuccess);
        h.Registry.Register(AiRunning(10));

        h.SeedRun("a");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(HaltReason.MaxAiTraversals, run.HaltReason);
        Assert.Equal(HumanFeedbackReasons.MaxAiTraversalsReached, run.HumanFeedbackReason);
        Assert.Null(run.CompletedAt);
        Assert.Equal(3, run.AiTraversalCount);
        // The cap stops the run BEFORE the node that would exceed it, so exactly
        // three AI nodes ran and none was left half-executed.
        var nodes = h.ReloadRunNodes();
        Assert.Equal(3, nodes.Count);
        Assert.All(nodes, rn => Assert.Equal(LoopRunNodeStatus.Succeeded, rn.Status));

        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.Is<string?>(r => r != null && r.Contains("3 steps without human input")),
            null, h.RunId, HumanFeedbackReasons.MaxAiTraversalsReached, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task The_counter_survives_a_restart()
    {
        using var h = new LoopEngineHarness();
        await SetCapAsync(h, 3);
        h.AddNode("a", NodeType.AI);
        h.AddEdge("a", "a", EdgeType.OnSuccess);
        h.Registry.Register(AiRunning(10));

        // A process that died after three AI nodes leaves the spent budget on the
        // row; the fresh driver must not hand the graph a new one.
        var seeded = h.SeedRun("a");
        seeded.AiTraversalCount = 3;
        h.Db.Context.SaveChanges();

        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(HaltReason.MaxAiTraversals, run.HaltReason);
        Assert.Empty(h.ReloadRunNodes());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("focus on the failing test")]
    public async Task Resuming_a_capped_run_refills_the_budget_and_it_carries_on(string? note)
    {
        using var h = new LoopEngineHarness();
        await SetCapAsync(h, 3);
        h.AddNode("a", NodeType.AI);
        h.AddEdge("a", "a", EdgeType.OnSuccess);
        h.Registry.Register(AiRunning(10));

        h.SeedRun("a");
        await h.RunAsync();
        Assert.Equal(3, h.ReloadRunNodes().Count);

        await h.Engine.ResumeFromHaltAsync(h.RunId, note);
        await h.WaitUntilIdleAsync();

        // Three more AI nodes ran on the refilled budget before the cap bit again.
        var run = h.ReloadRun();
        Assert.Equal(6, h.ReloadRunNodes().Count);
        Assert.Equal(HaltReason.MaxAiTraversals, run.HaltReason);
        Assert.Equal(3, run.AiTraversalCount);
        Assert.Equal(note ?? string.Empty, run.SteeringNote);
    }

    [Fact]
    public async Task The_cap_defaults_to_the_app_setting_default_when_unset()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("a", NodeType.AI);
        h.AddEdge("a", "a", EdgeType.OnSuccess);
        h.Registry.Register(AiRunning(AppSettingKeys.DefaultMaxAiTraversals + 5));

        h.SeedRun("a");
        await h.RunAsync();

        var run = h.ReloadRun();
        Assert.Equal(HaltReason.MaxAiTraversals, run.HaltReason);
        Assert.Equal(AppSettingKeys.DefaultMaxAiTraversals, run.AiTraversalCount);
    }
}
