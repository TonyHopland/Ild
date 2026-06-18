using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Covers the AI node's <c>forkFromPlaceholder</c> behavior: a fork-from node
/// re-seeds from the source session on every execution (fresh destination id),
/// ignores the destination's own prior binding, and falls back to a plain new
/// session when the source has nothing bound.
/// </summary>
public class AINodeExecutorForkTests
{
    /// <summary>Captures the context the executor hands the adapter, and echoes the dest id back as the session id.</summary>
    private sealed class CapturingAdapter : IAgentAdapter
    {
        public AgentExecutionContext? Captured { get; private set; }
        public string Name => "stub";
        public string[] SupportedProviderTypes => ["stub"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            Captured = context;
            return Task.FromResult(NodeExecutionResult.Ok("done", sessionId: context.SessionId));
        }
    }

    private static (IServiceProvider sp, CapturingAdapter adapter, Mock<ILoopRunStore> runStore) BuildServices(
        LoopRunSessionBinding? forkSourceBinding)
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "stub",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);

        var adapter = new CapturingAdapter();
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));

        var runStore = new Mock<ILoopRunStore>();
        runStore.Setup(s => s.GetSessionBindingAsync(It.IsAny<Guid>(), "AI", "base"))
            .ReturnsAsync(forkSourceBinding);

        var wi = new WorkItemView { Id = "WI-1", RepositoryId = null };
        var workItems = Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>()) == Task.FromResult<WorkItemView?>(wi));

        var services = new ServiceCollection();
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(runStore.Object);
        services.AddSingleton(workItems);
        services.AddSingleton(registry);
        return (services.BuildServiceProvider(), adapter, runStore);
    }

    private static LoopNode MakeNode(string configJson) => new()
    {
        Id = Guid.NewGuid(),
        NodeType = NodeType.AI,
        Config = configJson,
    };

    private static LoopRun MakeRun() => new() { Id = Guid.NewGuid(), WorkItemId = "WI-1" };

    private const string ForkConfig =
        @"{""useSession"":true,""sessionPlaceholder"":""fork"",""forkFromPlaceholder"":""base""}";

    [Fact]
    public async Task Fork_from_bound_source_passes_source_id_and_fresh_destination()
    {
        var sourceBinding = new LoopRunSessionBinding
        {
            AdapterName = "AI",
            PlaceholderId = "base",
            SessionId = "source-sess-id",
        };
        var (sp, adapter, _) = BuildServices(sourceBinding);
        var executor = new AINodeExecutor();

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(MakeRun(), MakeNode(ForkConfig), sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.NotNull(adapter.Captured);
        // The source session is copied; a fresh, distinct destination id is used.
        Assert.Equal("source-sess-id", adapter.Captured!.ForkFromSessionId);
        Assert.False(string.IsNullOrWhiteSpace(adapter.Captured.SessionId));
        Assert.NotEqual("source-sess-id", adapter.Captured.SessionId);
        Assert.True(adapter.Captured.ManageSession);

        // The fork (not the source) is bound under the destination placeholder.
        var bound = Assert.IsType<NodeOutcome.SessionBound>(
            outcomes.Single(o => o is NodeOutcome.SessionBound));
        Assert.Equal("fork", bound.SessionPlaceholder);
        Assert.Equal(adapter.Captured.SessionId, bound.SessionId);
    }

    [Fact]
    public async Task Refork_uses_a_new_destination_id_each_execution()
    {
        var sourceBinding = new LoopRunSessionBinding
        {
            AdapterName = "AI",
            PlaceholderId = "base",
            SessionId = "source-sess-id",
        };
        var (sp, adapter, _) = BuildServices(sourceBinding);
        var executor = new AINodeExecutor();
        var run = MakeRun();
        var node = MakeNode(ForkConfig);

        await foreach (var _ in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None))) { }
        var first = adapter.Captured!.SessionId;

        await foreach (var _ in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None))) { }
        var second = adapter.Captured!.SessionId;

        // Re-fork-from-base: each run forks afresh rather than continuing the
        // previous fork, so the destination id differs between executions.
        Assert.NotEqual(first, second);
        Assert.Equal("source-sess-id", adapter.Captured.ForkFromSessionId);
    }

    [Fact]
    public async Task Fork_from_unbound_source_starts_fresh_without_forking()
    {
        var (sp, adapter, _) = BuildServices(forkSourceBinding: null);
        var executor = new AINodeExecutor();

        await foreach (var _ in executor.ExecuteAsync(new NodeExecutionContext(MakeRun(), MakeNode(ForkConfig), sp, CancellationToken.None))) { }

        Assert.NotNull(adapter.Captured);
        // Source has nothing bound → behave like a normal new AI node: no fork,
        // no inherited session id.
        Assert.Null(adapter.Captured!.ForkFromSessionId);
        Assert.Null(adapter.Captured.SessionId);
    }
}
