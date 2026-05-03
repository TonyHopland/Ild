using FluentAssertions;
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
    private sealed class TestAdapter : IAgentAdapter
    {
        public string Name => "TestAdapter";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("adapter-result"));
    }

    [Fact]
    public async Task ExecuteAsync_resolves_provider_by_name_delegates_to_adapter_and_returns_result()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new TestAdapter());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext("my-provider"));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("adapter-result");
    }

    [Fact]
    public async Task ExecuteAsync_resolves_provider_by_guid_when_config_contains_id()
    {
        var providerId = Guid.NewGuid();
        var provider = new AiProvider
        {
            Id = providerId,
            Name = "by-id-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByIdAsync(providerId))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new TestAdapter());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext(providerId.ToString("D")));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("adapter-result");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_default_provider_when_no_provider_key()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new TestAdapter());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext(null));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("adapter-result");
    }

    [Fact]
    public async Task ExecuteAsync_falls_back_to_first_provider_when_no_default()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "first-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync((AiProvider?)null);
        providerStore.Setup(s => s.GetFirstAiProviderAsync())
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new TestAdapter());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext(null));

        result.Success.Should().BeTrue();
        result.Output.Should().Be("adapter-result");
    }

    [Fact]
    public async Task ExecuteAsync_when_loop_prompt_not_set_uses_initial_prompt_for_all_executions()
    {
        var nodeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var testAdapter = new CapturingAdapter();

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => testAdapter);

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(new List<LoopRunNode>
            {
                new LoopRunNode { LoopNodeId = nodeId }
            });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        // Config has initialPrompt but no loopPrompt
        var config = $"{{\"provider\":\"my-provider\",\"initialPrompt\":\"first prompt here\"}}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItem { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedInitialPrompt.Should().Be("first prompt here");
        testAdapter.CapturedLoopPrompt.Should().Be("first prompt here");
    }

    [Fact]
    public async Task ExecuteAsync_on_loopback_visit_passes_execution_count_of_2()
    {
        var providerId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var otherNodeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var provider = new AiProvider
        {
            Id = providerId,
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var testAdapter = new CapturingAdapter();

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => testAdapter);

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(new List<LoopRunNode>
            {
                new LoopRunNode { LoopNodeId = nodeId },
                new LoopRunNode { LoopNodeId = otherNodeId },
                new LoopRunNode { LoopNodeId = nodeId }
            });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var ctx = BuildNodeExecutionContextWithIds("my-provider", runId, nodeId);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_passes_execution_count_based_on_node_visit_count_from_store()
    {
        var providerId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var provider = new AiProvider
        {
            Id = providerId,
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var testAdapter = new CapturingAdapter();

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => testAdapter);

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(new List<LoopRunNode>
            {
                new LoopRunNode { LoopNodeId = nodeId }
            });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        // RetryCount=2 would give ExecutionCount=3 with old logic.
        // Store returns 1 node for this nodeId, so ExecutionCount should be 1.
        var ctx = BuildNodeExecutionContextWithIdsAndRetry("my-provider", runId, nodeId, retryCount: 2);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedCount.Should().Be(1);
        loopRunStore.Verify(s => s.GetRunNodesAsync(runId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_propagates_adapter_failure()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new FailingAdapter());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext("my-provider"));

        result.Success.Should().BeFalse();
        result.Error.Should().Be("something went wrong");
    }

    [Fact]
    public async Task ExecuteAsync_times_out_when_adapter_exceeds_node_timeout()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new SlowAdapter());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var config = "{\"provider\":\"my-provider\",\"prompt\":\"test prompt\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Config = config, TimeoutSeconds = 1 },
            WorkItem: new WorkItem { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_when_adapter_finishes_within_timeout()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new TestAdapter());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var config = "{\"provider\":\"my-provider\",\"prompt\":\"test prompt\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Config = config, TimeoutSeconds = 300 },
            WorkItem: new WorkItem { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("adapter-result");
    }

    [Fact]
    public async Task ExecuteAsync_returns_fail_when_no_provider_found()
    {
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync((AiProvider?)null);
        providerStore.Setup(s => s.GetFirstAiProviderAsync())
            .ReturnsAsync((AiProvider?)null);

        var registry = new Mock<IAgentAdapterRegistry>();

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext(null));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No AI provider found");
    }

    [Fact]
    public async Task ExecuteAsync_passes_adapter_config_to_adapter()
    {
        var nodeId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model"
        };

        var testAdapter = new CapturingAdapter();

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => testAdapter);

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"provider":"my-provider","prompt":"test prompt","adapterConfig":{"temperature":0.5,"maxTokens":8192}}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItem { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedAdapterConfig.Should().NotBeNull();
        testAdapter.CapturedAdapterConfig!["temperature"].Should().Be(0.5);
        testAdapter.CapturedAdapterConfig!["maxTokens"].Should().Be(8192);
    }

    private sealed class CapturingAdapter : IAgentAdapter
    {
        public int CapturedCount;
        public string? CapturedInitialPrompt;
        public string? CapturedLoopPrompt;
        public Dictionary<string, object?>? CapturedAdapterConfig;

        public string Name => "Capturing";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
        {
            CapturedCount = ctx.ExecutionCount;
            CapturedInitialPrompt = ctx.InitialPrompt;
            CapturedLoopPrompt = ctx.LoopPrompt;
            CapturedAdapterConfig = ctx.AdapterConfig;
            return Task.FromResult(NodeExecutionResult.Ok("ok"));
        }
    }

    private sealed class FailingAdapter : IAgentAdapter
    {
        public string Name => "Failing";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Fail("something went wrong"));
    }

    private sealed class SlowAdapter : IAgentAdapter
    {
        public string Name => "Slow";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ctx.Cancel);
            return NodeExecutionResult.Ok("should-not-reach");
        }
    }

    private static IServiceProvider BuildServiceProvider(
        IProviderStore providerStore,
        IAgentAdapterRegistry registry,
        ILoopRunStore? loopRunStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(providerStore);
        services.AddSingleton(registry);
        if (loopRunStore == null)
        {
            var defaultStore = new Mock<ILoopRunStore>();
            defaultStore.Setup(s => s.GetRunNodesAsync(It.IsAny<Guid>()))
                .ReturnsAsync(Array.Empty<LoopRunNode>());
            loopRunStore = defaultStore.Object;
        }
        services.AddSingleton(loopRunStore);

        var eventLogService = new Mock<IEventLogService>();
        eventLogService.Setup(s => s.GetByRunIdAsync(It.IsAny<Guid>(), null))
            .ReturnsAsync(Array.Empty<EventLogEntry>());
        services.AddSingleton(eventLogService.Object);

        return services.BuildServiceProvider();
    }

    private static NodeExecutionContext BuildNodeExecutionContext(string? providerName)
        => BuildNodeExecutionContextWithRetry(providerName, retryCount: 0);

    private static NodeExecutionContext BuildNodeExecutionContextWithRetry(string? providerName, int retryCount)
    {
        var config = providerName != null
            ? $"{{\"provider\":\"{providerName}\",\"prompt\":\"test prompt\"}}"
            : "{\"prompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = retryCount },
            Node: new LoopNode { Config = config },
            WorkItem: new WorkItem { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }

    private static NodeExecutionContext BuildNodeExecutionContextWithIds(string? providerName, Guid runId, Guid nodeId)
        => BuildNodeExecutionContextWithIdsAndRetry(providerName, runId, nodeId, retryCount: 0);

    private static NodeExecutionContext BuildNodeExecutionContextWithIdsAndRetry(string? providerName, Guid runId, Guid nodeId, int retryCount)
    {
        var config = providerName != null
            ? $"{{\"provider\":\"{providerName}\",\"prompt\":\"test prompt\"}}"
            : "{\"prompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = retryCount },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItem { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }
}
