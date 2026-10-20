using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A node whose prompt uses a global variable that does not exist, or whose value
/// is empty, parks the run for a person before that node starts — it neither runs
/// the node nor fails it — and Resume checks again against the values as they are then.
/// </summary>
public class LoopEngineGlobalVariableHaltTests
{
    private const string PagePath = "/global-variables";

    private static void SetGlobal(LoopEngineHarness h, string name, string value)
    {
        var existing = h.Db.Context.Set<GlobalVariable>()
            .FirstOrDefault(g => g.NormalizedName == name.ToUpperInvariant());
        if (existing is null)
        {
            h.Db.Context.Set<GlobalVariable>().Add(new GlobalVariable
            {
                Id = Guid.NewGuid(),
                Name = name,
                NormalizedName = name.ToUpperInvariant(),
                Value = value,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        h.Db.Context.SaveChanges();
    }

    private static void DeleteGlobal(LoopEngineHarness h, string name)
    {
        var set = h.Db.Context.Set<GlobalVariable>();
        set.Remove(set.Single(g => g.NormalizedName == name.ToUpperInvariant()));
        h.Db.Context.SaveChanges();
    }

    private static ScriptedExecutor Succeeding(NodeType type)
        => new(type, new NodeOutcome.NodeStarting(type.ToString()), new NodeOutcome.Success(EdgeType.OnSuccess, "ok"));

    /// <summary>
    /// Start → n → done, plus an on_failure edge from n to a node that must never run.
    /// Returns the executor registered for n.
    /// </summary>
    private static ScriptedExecutor Graph(LoopEngineHarness h, NodeType type, string config, string label = "Writer")
    {
        h.AddNode("n", type, label);
        h.NodesById["n"].Config = config;
        h.AddNode("done", NodeType.Cleanup);
        h.AddNode("fallback", NodeType.Cmd);
        h.AddEdge("n", "done", EdgeType.OnSuccess);
        h.AddEdge("n", "fallback", EdgeType.OnFailure);
        h.AddEdge("fallback", "done", EdgeType.OnSuccess);
        h.Db.Context.SaveChanges();

        var executor = Succeeding(type);
        h.Registry.Register(executor);
        h.Registry.Register(Succeeding(NodeType.Cmd));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"), new NodeOutcome.Terminal("done")));
        return executor;
    }

    private static void AssertParkedBeforeTheNode(LoopEngineHarness h, ScriptedExecutor executor)
    {
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.True(run.IsHalted);
        Assert.Equal(HaltReason.GlobalVariableMissing, run.HaltReason);
        Assert.Equal(HumanFeedbackReasons.GlobalVariableMissing, run.HumanFeedbackReason);
        Assert.Null(run.CompletedAt);
        Assert.Equal(h.NodesById["n"].Id, run.CurrentNodeId);
        Assert.Equal(0, executor.Invocations);
        Assert.Empty(h.ReloadRunNodes());
    }

    public static TheoryData<NodeType, string> EveryRenderedField => new()
    {
        { NodeType.AI, """{"prompt":"Follow {{Global.Rules}}"}""" },
        { NodeType.Human, """{"prompt":"Check against {{Global.Rules}}"}""" },
        { NodeType.Prompt, """{"prompt":"{{Global.Rules}}"}""" },
        { NodeType.PR, """{"prompt":"Review with {{Global.Rules}}"}""" },
        { NodeType.PR, """{"prompt":"Open it","prDescriptionTemplate":"Written per {{Global.Rules}}"}""" },
        { NodeType.Condition, """{"output":"{{ Global.rules }}","cases":[],"defaultEdge":"Default"}""" },
        {
            NodeType.Condition,
            """{"cases":[{"variant":"PrExists","edgeName":"HasPr"},{"variant":"TextMatches","subject":"{{Global.Rules}}","pattern":"x","edgeName":"Hit"}],"defaultEdge":"Default"}"""
        },
    };

    [Theory]
    [MemberData(nameof(EveryRenderedField))]
    public async Task A_missing_global_in_any_field_the_node_renders_parks_the_run_before_the_node_starts(NodeType type, string config)
    {
        using var h = new LoopEngineHarness();
        var executor = Graph(h, type, config);
        var seeded = h.SeedRun("n");
        seeded.CurrentAiSessionId = "sess-of-an-earlier-node";
        h.Db.Context.SaveChanges();

        await h.RunAsync();

        AssertParkedBeforeTheNode(h, executor);
        Assert.Null(h.ReloadRun().CurrentAiSessionId);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.Is<string?>(r => r != null && r.Contains("Rules", StringComparison.OrdinalIgnoreCase) && r.Contains(PagePath)),
            It.IsAny<string?>(), h.RunId, HumanFeedbackReasons.GlobalVariableMissing), Times.Once);
    }

    [Fact]
    public async Task The_park_names_every_missing_or_empty_global_and_the_page_to_set_them_on()
    {
        using var h = new LoopEngineHarness();
        SetGlobal(h, "Beta", "");
        SetGlobal(h, "Gamma", "set");
        var executor = Graph(h, NodeType.AI,
            """{"prompt":"{{Global.Alpha}} {{Global.Beta}} {{Global.Gamma}} {{Global.Alpha}}"}""");
        h.SeedRun("n");

        await h.RunAsync();

        AssertParkedBeforeTheNode(h, executor);
        var parked = Assert.Single(h.ReloadEvents(), e => e.EventType == EventType.RunParked);
        Assert.Contains("Alpha", parked.Data!);
        Assert.Contains("Beta", parked.Data!);
        Assert.DoesNotContain("Gamma", parked.Data!);
        Assert.Contains(PagePath, parked.Data!);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            It.Is<string?>(r => r != null && r.Contains("Alpha") && r.Contains("Beta") && !r.Contains("Gamma") && r.Contains(PagePath)),
            It.IsAny<string?>(), h.RunId, HumanFeedbackReasons.GlobalVariableMissing), Times.Once);
    }

    [Fact]
    public async Task A_node_whose_globals_all_have_values_runs_whatever_the_case_of_the_name()
    {
        using var h = new LoopEngineHarness();
        SetGlobal(h, "Rules", "be brief");
        // Only "" counts as empty; a value of spaces is a value somebody set.
        SetGlobal(h, "Spacing", "  ");
        var executor = Graph(h, NodeType.AI, """{"prompt":"{{Global.RULES}} {{ Global.spacing }}"}""");
        h.SeedRun("n");

        await h.RunAsync();

        Assert.Equal(1, executor.Invocations);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Null(run.HaltReason);
    }

    [Fact]
    public async Task A_PR_description_is_not_checked_once_the_run_already_has_a_PR()
    {
        using var h = new LoopEngineHarness();
        var executor = Graph(h, NodeType.PR, """{"prompt":"Open it","prDescriptionTemplate":"{{Global.Rules}}"}""");
        var seeded = h.SeedRun("n");
        seeded.PrUrl = "https://git.example.com/example/repo/pulls/1";
        h.Db.Context.SaveChanges();

        await h.RunAsync();

        Assert.Equal(1, executor.Invocations);
        Assert.False(h.ReloadRun().IsHalted);
    }

    [Fact]
    public async Task An_AI_node_steered_back_into_its_live_session_is_not_checked()
    {
        using var h = new LoopEngineHarness();
        var executor = Graph(h, NodeType.AI, """{"prompt":"{{Global.Rules}}"}""");
        var seeded = h.SeedRun("n");
        seeded.SteeringNote = "keep going";
        seeded.CurrentAiSessionId = "sess-live";
        h.Db.Context.SaveChanges();

        await h.RunAsync();

        Assert.Equal(1, executor.Invocations);
        Assert.False(h.ReloadRun().IsHalted);
    }

    [Fact]
    public async Task A_Human_node_answered_after_its_global_was_deleted_carries_on()
    {
        using var h = new LoopEngineHarness();
        SetGlobal(h, "Rules", "be brief");
        h.AddNode("human", NodeType.Human, "Ask");
        h.NodesById["human"].Config = """{"prompt":"Check {{Global.Rules}}"}""";
        h.AddNode("done", NodeType.Cleanup);
        h.AddEdge("human", "done", EdgeType.Custom, "Respond");
        h.Db.Context.SaveChanges();
        var human = new ScriptedExecutor(NodeType.Human,
                new NodeOutcome.NodeStarting("ask"),
                new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, "prompt"))
            .Then(new NodeOutcome.Success(EdgeType.Custom, "answer", "Respond"));
        h.Registry.Register(human);
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"), new NodeOutcome.Terminal("done")));
        h.SeedRun("human");
        await h.RunAsync();
        Assert.Equal(LoopRunStatus.WaitingHuman, h.ReloadRun().Status);

        DeleteGlobal(h, "Rules");
        var waiting = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);
        await h.Engine.SignalNodeResultAsync(h.RunId, waiting.Id, NodeSignal.Respond("answer"));
        await h.WaitUntilIdleAsync();

        Assert.Equal(2, human.Invocations);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Null(run.HaltReason);
    }

    [Fact]
    public async Task Resume_checks_again_and_parks_again_until_the_global_is_set_then_the_node_runs()
    {
        using var h = new LoopEngineHarness();
        var executor = Graph(h, NodeType.Prompt, """{"prompt":"{{Global.Rules}}"}""");
        h.SeedRun("n");
        await h.RunAsync();
        AssertParkedBeforeTheNode(h, executor);

        await h.Engine.ResumeFromHaltAsync(h.RunId, null);
        await h.WaitUntilIdleAsync();

        AssertParkedBeforeTheNode(h, executor);
        Assert.Equal(2, h.ReloadEvents().Count(e => e.EventType == EventType.RunParked));

        SetGlobal(h, "rules", "be brief");
        await h.Engine.ResumeFromHaltAsync(h.RunId, "now it is set");
        await h.WaitUntilIdleAsync();

        Assert.Equal(1, executor.Invocations);
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Null(run.HaltReason);
        Assert.False(run.IsHalted);
    }
}
