using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class ConditionNodeExecutorTests
{
    /// <summary>
    /// Stand-in renderer that resolves only the one placeholder these tests
    /// exercise (<c>{{Node.Input}}</c> → previous-node output), mirroring the
    /// real resolver closely enough to prove pass-through behaviour.
    /// </summary>
    private sealed class FakeRendering : IPromptRenderingService
    {
        public Task<string> RenderAsync(string? template, Guid runId, WorkItemView workItem, string? previousNodeOutput)
            => Task.FromResult((template ?? string.Empty).Replace("{{Node.Input}}", previousNodeOutput ?? string.Empty));
    }

    private static LoopRun MakeRun(string? previousOutput = null, string? prUrl = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkItemId = "WI-1",
        PreviousNodeOutput = previousOutput,
        PrUrl = prUrl,
    };

    /// <summary>Builds one switch case config dict, omitting unused predicate keys.</summary>
    private static Dictionary<string, object?> Case(
        string variant, string edgeName, string? pattern = null, string? tag = null, string? subject = null)
    {
        var c = new Dictionary<string, object?> { ["variant"] = variant, ["edgeName"] = edgeName };
        if (pattern != null) c["pattern"] = pattern;
        if (tag != null) c["tag"] = tag;
        if (subject != null) c["subject"] = subject;
        return c;
    }

    /// <summary>Builds a Condition switch node from its cases and default edge.</summary>
    private static LoopNode SwitchNode(object[] cases, string defaultEdge, string? output = null)
    {
        var config = new Dictionary<string, object?> { ["cases"] = cases, ["defaultEdge"] = defaultEdge };
        if (output != null) config["output"] = output;
        var json = System.Text.Json.JsonSerializer.Serialize(config);
        return new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Condition, Config = json };
    }

    private static IServiceProvider BuildServices(WorkItemView? workItem)
    {
        var wim = new Mock<IWorkItemManager>();
        wim.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);

        var services = new ServiceCollection();
        services.AddSingleton(wim.Object);
        services.AddSingleton<IPromptRenderingService>(new FakeRendering());
        return services.BuildServiceProvider();
    }

    private static WorkItemView Wi(params string[] tags) => new()
    {
        Id = "WI-1",
        Title = "Title",
        Tags = tags,
    };

    private static async Task<List<NodeOutcome>> RunAsync(LoopNode node, LoopRun run, IServiceProvider sp)
    {
        var exec = new ConditionNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in exec.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);
        return outcomes;
    }

    private static NodeOutcome.Success AssertSuccess(List<NodeOutcome> outcomes, string expectedEdge)
    {
        Assert.Contains(outcomes, o => o is NodeOutcome.NodeStarting);
        var success = Assert.IsType<NodeOutcome.Success>(outcomes[^1]);
        Assert.Equal(EdgeType.Custom, success.Edge);
        Assert.Equal(expectedEdge, success.EdgeName);
        return success;
    }

    [Fact]
    public async Task TextMatches_case_routes_to_its_edge_when_the_pattern_matches()
    {
        var node = SwitchNode(new object[] { Case("TextMatches", "matched", pattern: "approve") }, "unmatched");
        var run = MakeRun(previousOutput: "Please APPROVE this");
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        var success = AssertSuccess(outcomes, "matched");
        // Default Output is a pass-through of the node input.
        Assert.Equal("Please APPROVE this", success.Output);
    }

    [Fact]
    public async Task No_case_matches_routes_to_the_default_edge()
    {
        var node = SwitchNode(new object[] { Case("TextMatches", "matched", pattern: "approve") }, "unmatched");
        var run = MakeRun(previousOutput: "Rejected outright");
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        AssertSuccess(outcomes, "unmatched");
    }

    [Fact]
    public async Task PrExists_case_routes_by_whether_the_run_has_a_pr_url()
    {
        var node = SwitchNode(new object[] { Case("PrExists", "has-pr") }, "no-pr");

        AssertSuccess(await RunAsync(node, MakeRun(prUrl: "https://example.test/pr/1"), BuildServices(Wi())), "has-pr");
        AssertSuccess(await RunAsync(node, MakeRun(prUrl: null), BuildServices(Wi())), "no-pr");
    }

    [Fact]
    public async Task HasTag_case_matches_the_work_item_tag_case_insensitively()
    {
        var node = SwitchNode(new object[] { Case("HasTag", "tagged", tag: "Needs-Review") }, "untagged");

        AssertSuccess(await RunAsync(node, MakeRun(), BuildServices(Wi("backend", "needs-review"))), "tagged");
        AssertSuccess(await RunAsync(node, MakeRun(), BuildServices(Wi("backend"))), "untagged");
    }

    [Fact]
    public async Task Switch_evaluates_cases_in_order_and_the_first_match_wins()
    {
        // Both cases would match "banana"; the earlier one must win.
        var node = SwitchNode(
            new object[]
            {
                Case("TextMatches", "first", pattern: "a"),
                Case("TextMatches", "second", pattern: "a"),
            },
            "otherwise");
        var outcomes = await RunAsync(node, MakeRun(previousOutput: "banana"), BuildServices(Wi()));

        AssertSuccess(outcomes, "first");
    }

    [Fact]
    public async Task Output_template_is_emitted_identically_on_matched_and_default_branches()
    {
        var node = SwitchNode(
            new object[] { Case("TextMatches", "yes", pattern: "approve") },
            "no",
            output: "decided: {{Node.Input}}");

        var matched = await RunAsync(node, MakeRun(previousOutput: "approve"), BuildServices(Wi()));
        var defaulted = await RunAsync(node, MakeRun(previousOutput: "no"), BuildServices(Wi()));

        Assert.Equal("decided: approve", AssertSuccess(matched, "yes").Output);
        Assert.Equal("decided: no", AssertSuccess(defaulted, "no").Output);
    }

    [Fact]
    public async Task Missing_work_item_fails_on_failure()
    {
        var node = SwitchNode(new object[] { Case("PrExists", "has-pr") }, "no-pr");
        var outcomes = await RunAsync(node, MakeRun(), BuildServices(workItem: null));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("WorkItem not found", fail.Reason);
    }

    [Fact]
    public async Task Invalid_regex_in_a_case_fails_on_failure()
    {
        // Edge case: a pattern that slipped past save-time validation still
        // routes to OnFailure rather than throwing out of the engine or
        // silently falling through to the default edge.
        var node = SwitchNode(new object[] { Case("TextMatches", "matched", pattern: "**approve**") }, "unmatched");
        var run = MakeRun(previousOutput: "approve");
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("Invalid regex", fail.Reason);
    }

    [Fact]
    public async Task Unknown_variant_in_a_case_fails_on_failure()
    {
        var node = SwitchNode(new object[] { Case("Bogus", "x") }, "otherwise");
        var outcomes = await RunAsync(node, MakeRun(), BuildServices(Wi()));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("Unknown condition variant", fail.Reason);
    }
}
