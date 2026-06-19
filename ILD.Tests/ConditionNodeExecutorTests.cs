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

    private static LoopNode MakeNode(Dictionary<string, object?> config)
    {
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
    public async Task TextMatches_routes_true_when_pattern_matches()
    {
        var node = MakeNode(new() { ["variant"] = "TextMatches", ["pattern"] = "approve" });
        var run = MakeRun(previousOutput: "Please APPROVE this");
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        var success = AssertSuccess(outcomes, "true");
        // Default Output is pass-through of the node input.
        Assert.Equal("Please APPROVE this", success.Output);
    }

    [Fact]
    public async Task TextMatches_routes_false_when_pattern_does_not_match()
    {
        var node = MakeNode(new() { ["variant"] = "TextMatches", ["pattern"] = "approve" });
        var run = MakeRun(previousOutput: "Rejected outright");
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        AssertSuccess(outcomes, "false");
    }

    [Fact]
    public async Task PrExists_routes_true_when_run_has_pr_url()
    {
        var node = MakeNode(new() { ["variant"] = "PrExists" });
        var run = MakeRun(prUrl: "https://example.test/pr/1");
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        AssertSuccess(outcomes, "true");
    }

    [Fact]
    public async Task PrExists_routes_false_when_run_has_no_pr_url()
    {
        var node = MakeNode(new() { ["variant"] = "PrExists" });
        var run = MakeRun(prUrl: null);
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        AssertSuccess(outcomes, "false");
    }

    [Fact]
    public async Task HasTag_routes_true_case_insensitively()
    {
        var node = MakeNode(new() { ["variant"] = "HasTag", ["tag"] = "Needs-Review" });
        var run = MakeRun();
        var outcomes = await RunAsync(node, run, BuildServices(Wi("backend", "needs-review")));

        AssertSuccess(outcomes, "true");
    }

    [Fact]
    public async Task HasTag_routes_false_when_tag_absent()
    {
        var node = MakeNode(new() { ["variant"] = "HasTag", ["tag"] = "needs-review" });
        var run = MakeRun();
        var outcomes = await RunAsync(node, run, BuildServices(Wi("backend")));

        AssertSuccess(outcomes, "false");
    }

    [Fact]
    public async Task Output_template_is_emitted_identically_on_both_branches()
    {
        var node = MakeNode(new()
        {
            ["variant"] = "TextMatches",
            ["pattern"] = "approve",
            ["output"] = "decided: {{Node.Input}}",
        });

        var trueOutcomes = await RunAsync(node, MakeRun(previousOutput: "approve"), BuildServices(Wi()));
        var falseOutcomes = await RunAsync(node, MakeRun(previousOutput: "no"), BuildServices(Wi()));

        var t = AssertSuccess(trueOutcomes, "true");
        var f = AssertSuccess(falseOutcomes, "false");
        Assert.Equal("decided: approve", t.Output);
        Assert.Equal("decided: no", f.Output);
    }

    [Fact]
    public async Task Missing_work_item_fails_on_failure()
    {
        var node = MakeNode(new() { ["variant"] = "PrExists" });
        var outcomes = await RunAsync(node, MakeRun(), BuildServices(workItem: null));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("WorkItem not found", fail.Reason);
    }

    [Fact]
    public async Task Invalid_regex_at_runtime_fails_on_failure()
    {
        // Edge case: a pattern that slipped past save-time validation still
        // routes to OnFailure rather than throwing out of the engine.
        var node = MakeNode(new() { ["variant"] = "TextMatches", ["pattern"] = "**approve**" });
        var run = MakeRun(previousOutput: "approve");
        var outcomes = await RunAsync(node, run, BuildServices(Wi()));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("Invalid regex", fail.Reason);
    }

    [Fact]
    public async Task Unknown_variant_fails_on_failure()
    {
        var node = MakeNode(new() { ["variant"] = "Bogus" });
        var outcomes = await RunAsync(node, MakeRun(), BuildServices(Wi()));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("Unknown condition variant", fail.Reason);
    }
}
