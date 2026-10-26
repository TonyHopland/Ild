using System.Text.Json;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A Set Variable node renders every assignment value and hands the engine the
/// whole set to write in one outcome; it routes OnSuccess and passes its input
/// through as its Output.
/// </summary>
public class SetVariableNodeExecutorTests
{
    /// <summary>Resolves <c>{{Node.Input}}</c> and <c>{{WorkItem.Title}}</c>, enough to tell rendered from raw.</summary>
    private sealed class FakeRendering : IPromptRenderingService
    {
        public Task<string> RenderAsync(string? template, Guid runId, WorkItemView workItem, string? previousNodeOutput)
            => Task.FromResult((template ?? string.Empty)
                .Replace("{{Node.Input}}", previousNodeOutput ?? string.Empty)
                .Replace("{{WorkItem.Title}}", workItem.Title));
    }

    private static LoopNode Node(object assignments) => new()
    {
        Id = Guid.NewGuid(),
        NodeType = NodeType.SetVariable,
        Config = JsonSerializer.Serialize(new { assignments }),
    };

    private static readonly WorkItemView WorkItem = new() { Id = "WI-1", Title = "Fix the widget" };

    private static IServiceProvider Services(WorkItemView? workItem)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton<IPromptRenderingService>(new FakeRendering());
        return services.BuildServiceProvider();
    }

    private static async Task<List<NodeOutcome>> RunAsync(LoopNode node, string? input, bool workItemExists = true)
    {
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", PreviousNodeOutput = input };
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in new SetVariableNodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, Services(workItemExists ? WorkItem : null), CancellationToken.None)))
            outcomes.Add(o);
        return outcomes;
    }

    [Fact]
    public async Task Renders_every_value_as_is_and_passes_its_input_through_on_success()
    {
        var node = Node(new object[]
        {
            new { name = "handoff", value = "{{Node.Input}}" },
            new { name = "ticket", value = "  {{WorkItem.Title}}" },
            new { name = "cleared", value = "" },
            new { name = "unset", value = (string?)null },
        });

        var outcomes = await RunAsync(node, "answer\n");

        Assert.Equal("answer\n", Assert.IsType<NodeOutcome.NodeStarting>(outcomes[0]).EffectiveInput);
        var assigned = Assert.IsType<NodeOutcome.VariablesAssigned>(outcomes[1]);
        Assert.Equal(
            new[] { ("handoff", "answer\n"), ("ticket", "  Fix the widget"), ("cleared", ""), ("unset", "") },
            assigned.Assignments.Select(a => (a.Name, a.Value)));
        var success = Assert.IsType<NodeOutcome.Success>(outcomes[2]);
        Assert.Equal(EdgeType.OnSuccess, success.Edge);
        Assert.Equal("answer\n", success.Output);
        Assert.Equal(3, outcomes.Count);
    }

    [Fact]
    public async Task Fails_and_assigns_nothing_when_the_work_item_is_gone()
    {
        var outcomes = await RunAsync(Node(new[] { new { name = "ticket", value = "{{WorkItem.Title}}" } }), "in", workItemExists: false);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.VariablesAssigned);
        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes[^1]);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("WorkItem not found", fail.Reason);
    }
}
