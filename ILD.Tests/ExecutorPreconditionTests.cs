using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The real executors' precondition paths, driven through the engine. A node
/// that fails or defers before yielding NodeStarting must not leave a
/// LoopRunNode row for an execution that never happened.
/// </summary>
public class ExecutorPreconditionTests
{
    private sealed class CountingAdapter : IAgentAdapter
    {
        public int Calls { get; private set; }
        public string Name => "Counting";
        public string[] SupportedProviderTypes => ["claude-code"];
        public ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Calls++;
            return Task.FromResult(NodeExecutionResult.Ok("done", context.Prompt, null));
        }
    }

    [Fact]
    public async Task PRNode_without_a_repository_fails_without_creating_a_LoopRunNode()
    {
        const string reason = "PR node requires a repository on the work item";
        using var h = new LoopEngineHarness(configure: services =>
            services.AddSingleton(new Mock<IProviderStore>().Object));
        h.AddNode("pr", NodeType.PR);
        h.Registry.Register(new PRNodeExecutor());
        h.SeedRun("pr");

        await h.RunAsync();

        Assert.Empty(h.ReloadRunNodes());
        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Equal(HumanFeedbackReasons.NodeFailed, run.HumanFeedbackReason);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.HumanFeedback,
            reason, It.IsAny<string?>(), h.RunId, HumanFeedbackReasons.NodeFailed), Times.Once);
    }

    [Fact]
    public async Task AINode_with_its_provider_at_capacity_defers_without_creating_a_LoopRunNode_or_calling_the_adapter()
    {
        var adapter = new CountingAdapter();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "claude-code",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);
        using var h = new LoopEngineHarness(configure: services =>
        {
            services.AddSingleton(providerStore.Object);
            services.AddSingleton<IAgentAdapterRegistry>(new SingleAdapterRegistry(adapter));
            services.AddSingleton<IAiProviderConcurrencyTracker>(new AlwaysFullTracker());
        });
        h.AddNode("ai", NodeType.AI);
        h.Registry.Register(new AINodeExecutor());
        h.SeedRun("ai");

        await h.RunAsync();

        Assert.Empty(h.ReloadRunNodes());
        Assert.Equal(0, adapter.Calls);
        h.WorkItemsMock.Verify(m => m.TransitionAsync(
            It.IsAny<WorkItemActor>(), h.WorkItemId, RemoteWorkItemStatus.WaitingForIld,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task CmdNode_without_a_worktree_records_one_failed_LoopRunNode_carrying_the_command()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("cmd", NodeType.Cmd);
        h.NodesById["cmd"].Config = "{\"command\":\"echo hi\"}";
        h.Db.Context.SaveChanges();
        h.Registry.Register(new CmdNodeExecutor());
        h.SeedRun("cmd");

        await h.RunAsync();

        var node = Assert.Single(h.ReloadRunNodes());
        Assert.Equal(LoopRunNodeStatus.Failed, node.Status);
        Assert.Equal("echo hi", node.EffectiveInput);
        Assert.Contains("worktree", node.Error, StringComparison.OrdinalIgnoreCase);
    }
}
