using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class AINodeExecutorTests
{
    private static NodeExecutionContext BuildCtx(
        LoopNode node,
        LoopRun run,
        IServiceProvider sp)
        => new(run, node, sp, CancellationToken.None);

    private static LoopRun MakeRun() => new()
    {
        Id = Guid.NewGuid(),
        WorkItemId = "WI-1",
    };

    private static LoopNode MakeNode(string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        NodeType = NodeType.AI,
        Config = configJson,
    };

    private static IServiceProvider BuildServices(
        IProviderStore providerStore,
        ILoopRunStore? loopRunStore = null,
        IWorkItemManager? workItemManager = null)
    {
        var wi = new WorkItemView { Id = "WI-1", RepositoryId = null };
        var wimMock = workItemManager ?? Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>()) == Task.FromResult<WorkItemView?>(wi));

        var lrsMock = loopRunStore ?? Mock.Of<ILoopRunStore>(m =>
            m.GetByIdAsync(It.IsAny<Guid>()) == Task.FromResult<LoopRun?>(null) &&
            m.GetRunNodesAsync(It.IsAny<Guid>()) == Task.FromResult<IReadOnlyList<LoopRunNode>>(Array.Empty<LoopRunNode>()));

        var services = new ServiceCollection();
        services.AddSingleton(providerStore);
        services.AddSingleton(lrsMock);
        services.AddSingleton(wimMock);
        // IAgentAdapterRegistry intentionally not registered — causes executor to
        // fail with "No agent adapter registry", which proves provider resolution succeeded.
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Empty_aiProviderId_uses_default_provider()
    {
        var defaultProvider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync(defaultProvider);

        var sp = BuildServices(providerStore.Object);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(@"{}"), MakeRun(), sp);

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(ctx))
            outcomes.Add(o);

        // Should not fail with "missing aiProviderId"
        Assert.DoesNotContain(outcomes, o =>
            o is NodeOutcome.Fail f && f.Reason.Contains("aiProviderId"));

        // Verify the default provider was looked up
        providerStore.Verify(s => s.GetDefaultAiProviderAsync(), Times.Once);
    }

    [Fact]
    public async Task Null_aiProviderId_and_no_default_provider_yields_descriptive_fail()
    {
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync((AiProvider?)null);

        var sp = BuildServices(providerStore.Object);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(null), MakeRun(), sp);

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(ctx))
            outcomes.Add(o);

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
        Assert.Contains("no default provider", fail.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explicit_aiProviderId_not_found_yields_fail()
    {
        var providerId = Guid.NewGuid();
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByIdAsync(providerId))
            .ReturnsAsync((AiProvider?)null);

        var sp = BuildServices(providerStore.Object);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(
            MakeNode($@"{{""aiProviderId"":""{providerId}""}}"),
            MakeRun(), sp);

        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(ctx))
            outcomes.Add(o);

        var fail = Assert.IsType<NodeOutcome.Fail>(outcomes.Last());
        Assert.Contains(providerId.ToString(), fail.Reason);
        // The default-provider path must NOT be called
        providerStore.Verify(s => s.GetDefaultAiProviderAsync(), Times.Never);
    }
}
