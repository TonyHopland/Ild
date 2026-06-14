using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
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
        IWorkItemManager? workItemManager = null,
        IAgentAdapterRegistry? registry = null)
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
        // When no registry is supplied the executor fails with "No agent adapter
        // registry", which proves provider resolution succeeded. Match-rule
        // routing tests pass a fake registry so a real output is produced.
        if (registry is not null)
            services.AddSingleton(registry);
        return services.BuildServiceProvider();
    }

    /// <summary>Adapter that returns a fixed result, ignoring the request.</summary>
    private sealed class StubAdapter(NodeExecutionResult result) : IAgentAdapter
    {
        public string Name => "stub";
        public string[] SupportedProviderTypes => ["stub"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context) => Task.FromResult(result);
    }

    private static IAgentAdapterRegistry RegistryReturning(NodeExecutionResult result)
    {
        var adapter = new StubAdapter(result);
        return Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));
    }

    private static (IServiceProvider sp, Mock<IProviderStore> store) BuildServicesWithDefaultProvider(
        NodeExecutionResult adapterResult)
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
        var sp = BuildServices(providerStore.Object, registry: RegistryReturning(adapterResult));
        return (sp, providerStore);
    }

    private static async Task<NodeOutcome> LastOutcomeAsync(string configJson, NodeExecutionResult adapterResult)
    {
        var (sp, _) = BuildServicesWithDefaultProvider(adapterResult);
        var executor = new AINodeExecutor();
        var ctx = BuildCtx(MakeNode(configJson), MakeRun(), sp);

        NodeOutcome? last = null;
        await foreach (var o in executor.ExecuteAsync(ctx))
            last = o;
        return last!;
    }

    // Two rules both match "REJECT and review" (the first case-insensitively);
    // first-match-wins must route to the earlier rule's edge.
    private const string TwoRuleConfig =
        @"{""matchRules"":[{""pattern"":""reject"",""edgeName"":""Reject""},{""pattern"":""review"",""edgeName"":""Review""}]}";

    [Fact]
    public async Task Matching_output_routes_to_first_matching_rules_custom_edge_case_insensitively()
    {
        var outcome = await LastOutcomeAsync(TwoRuleConfig, NodeExecutionResult.Ok("REJECT and review"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.Custom, success.Edge);
        // Earlier rule wins even though the later "review" rule also matches.
        Assert.Equal("Reject", success.EdgeName);
    }

    [Fact]
    public async Task Non_matching_output_falls_through_to_OnSuccess()
    {
        var outcome = await LastOutcomeAsync(TwoRuleConfig, NodeExecutionResult.Ok("all good, shipping it"));

        var success = Assert.IsType<NodeOutcome.Success>(outcome);
        Assert.Equal(EdgeType.OnSuccess, success.Edge);
        Assert.Null(success.EdgeName);
    }

    [Fact]
    public async Task Adapter_failure_routes_to_OnFailure()
    {
        var outcome = await LastOutcomeAsync(TwoRuleConfig, NodeExecutionResult.Fail("adapter blew up"));

        var fail = Assert.IsType<NodeOutcome.Fail>(outcome);
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
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
