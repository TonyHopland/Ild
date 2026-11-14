using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The AI node executor's side of "Waiting for ILD": an agent that ended its turn with processes still running yields
/// <see cref="NodeOutcome.WaitingChildren"/> instead of a finished node, and one that did not behaves exactly as before.
/// </summary>
public class AINodeExecutorWaitingChildrenTests
{
    private sealed class FixedAdapter(NodeExecutionResult result) : IAgentAdapter
    {
        public string Name => "Fixed";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context) => Task.FromResult(result);
    }

    private static async Task<List<NodeOutcome>> RunAsync(NodeExecutionResult result)
    {
        using var db = new TestDb();
        var providers = new Mock<IProviderStore>();
        providers.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(new AiProvider
        {
            Id = Guid.NewGuid(), Name = "default", Type = "claude-code", IsDefault = true, CreatedAt = DateTime.UtcNow,
        });
        var workItems = Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>()) == Task.FromResult<WorkItemView?>(new WorkItemView { Id = "WI-1", RepositoryId = null }));
        var services = new ServiceCollection();
        services.AddSingleton(NoPackageFeeds.Resolver);
        services.AddSingleton(providers.Object);
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(workItems);
        services.AddSingleton<IAgentAdapterRegistry>(new SingleAdapterRegistry(new FixedAdapter(result)));
        var sp = services.BuildServiceProvider();

        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = "WI-1", LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running, RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.AI, Config = "{\"prompt\":\"run the tests\"}" };
        var outcomes = new List<NodeOutcome>();
        await foreach (var outcome in new AINodeExecutor().ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(outcome);
        return outcomes;
    }

    [Fact]
    public async Task A_turn_that_ends_with_processes_running_is_not_a_finished_node()
    {
        var children = new[] { new RunChildProcess(4321, "dotnet test ILD.Tests") };

        var outcomes = await RunAsync(NodeExecutionResult.Ok("Waiting on the full run.", sessionId: "s1") with { LiveChildren = children });

        var waiting = Assert.IsType<NodeOutcome.WaitingChildren>(outcomes.Last());
        Assert.Equal(children, waiting.Children);
        // What the agent said this turn is kept with the node.
        Assert.Equal("Waiting on the full run.", waiting.Output);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Success or NodeOutcome.Fail or NodeOutcome.Terminal);
        Assert.True(waiting.EndsNodeWork);
    }

    [Fact]
    public async Task A_turn_that_ends_with_nothing_running_succeeds_as_before()
    {
        var outcomes = await RunAsync(NodeExecutionResult.Ok("All done.", sessionId: "s1"));

        var success = Assert.IsType<NodeOutcome.Success>(outcomes.Last());
        Assert.Equal("All done.", success.Output);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WaitingChildren);
    }

    [Fact]
    public async Task An_empty_list_of_children_is_no_children()
    {
        var outcomes = await RunAsync(NodeExecutionResult.Ok("All done.") with { LiveChildren = [] });

        Assert.IsType<NodeOutcome.Success>(outcomes.Last());
    }

    [Fact]
    public async Task A_failed_turn_fails_whatever_it_left_running()
    {
        var outcomes = await RunAsync(NodeExecutionResult.Fail("exit=2 stderr=boom") with { LiveChildren = [new RunChildProcess(1, "x")] });

        Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
    }
}
