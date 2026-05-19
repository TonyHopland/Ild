using ILD.Core.Services.Implementations;
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

        Assert.True(result.Success);
        Assert.Equal("adapter-result", result.Output);
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

        Assert.True(result.Success);
        Assert.Equal("adapter-result", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_no_provider_key_set()
    {
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync((AiProvider?)null);

        var registry = new Mock<IAgentAdapterRegistry>();

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext(null));

        Assert.False(result.Success);
        Assert.Contains("No AI provider found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_uses_default_provider_when_no_provider_key_set()
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

        Assert.True(result.Success);
        Assert.Equal("adapter-result", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_specified_provider_not_found()
    {
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync("nonexistent"))
            .ReturnsAsync((AiProvider?)null);

        var registry = new Mock<IAgentAdapterRegistry>();

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext("nonexistent"));

        Assert.False(result.Success);
        Assert.Contains("No AI provider found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_uses_prompt_config_for_adapter_context()
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

        var config = $"{{\"aiProviderId\":\"my-provider\",\"prompt\":\"first prompt here\"}}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal("first prompt here", testAdapter.CapturedPrompt);
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

        Assert.True(result.Success);
        Assert.Equal(2, testAdapter.CapturedCount);
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

        Assert.True(result.Success);
        Assert.Equal(1, testAdapter.CapturedCount);
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

        Assert.False(result.Success);
        Assert.Equal("something went wrong", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_when_adapter_finishes()
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

        var config = "{\"aiProviderId\":\"my-provider\",\"prompt\":\"test prompt\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal("adapter-result", result.Output);
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

        Assert.False(result.Success);
        Assert.Contains("No AI provider found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_when_output_matches_rejectPattern()
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

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new RejectingAdapter());

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = "{\"aiProviderId\":\"my-provider\",\"prompt\":\"test\",\"rejectPattern\":\"I cannot|I'm unable\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.False(result.Success);
        Assert.Contains("AI rejected", result.Error);
        Assert.Equal("I cannot complete this task", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_succeeds_when_output_does_not_match_rejectPattern()
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

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new TestAdapter());

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = "{\"aiProviderId\":\"my-provider\",\"prompt\":\"test\",\"rejectPattern\":\"REJECT\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal("adapter-result", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_rejectPattern_is_case_insensitive()
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

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => new TestAdapter());

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = "{\"aiProviderId\":\"my-provider\",\"prompt\":\"test\",\"rejectPattern\":\"cannot\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_passes_bound_placeholder_session_to_adapter_when_useSession_is_enabled()
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
            .ReturnsAsync(Array.Empty<LoopRunNode>());
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(new LoopRun { Id = runId });
        loopRunStore.Setup(s => s.GetSessionBindingAsync(runId, "Capturing", "research"))
            .ReturnsAsync(new LoopRunSessionBinding
            {
                LoopRunId = runId,
                AdapterName = "Capturing",
                PlaceholderId = "research",
                SessionId = "selected-session-456",
            });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","prompt":"test","useSession":true,"sessionPlaceholder":"research"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal("selected-session-456", testAdapter.CapturedSessionId);
        Assert.Equal("selected-session-456", testAdapter.CapturedIncomingSessionId);
    }

    [Fact]
    public async Task ExecuteAsync_when_useSession_and_binding_exists_uses_same_prompt_with_bound_session()
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
            .ReturnsAsync(Array.Empty<LoopRunNode>());
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(new LoopRun { Id = runId });
        loopRunStore.Setup(s => s.GetSessionBindingAsync(runId, "Capturing", "research"))
            .ReturnsAsync(new LoopRunSessionBinding
            {
                LoopRunId = runId,
                AdapterName = "Capturing",
                PlaceholderId = "research",
                SessionId = "bound-session"
            });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","useSession":true,"prompt":"default prompt","sessionPlaceholder":"research"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal("default prompt", testAdapter.CapturedPrompt);
        Assert.Equal("bound-session", testAdapter.CapturedSessionId);
    }

    [Fact]
    public async Task ExecuteAsync_when_useSession_and_binding_missing_uses_same_prompt_without_bound_session()
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
            .ReturnsAsync(Array.Empty<LoopRunNode>());
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(new LoopRun { Id = runId });
        loopRunStore.Setup(s => s.GetSessionBindingAsync(runId, "Capturing", "research"))
            .ReturnsAsync((LoopRunSessionBinding?)null);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","useSession":true,"prompt":"default prompt","sessionPlaceholder":"research"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Equal("default prompt", testAdapter.CapturedPrompt);
        Assert.Null(testAdapter.CapturedSessionId);
    }

    [Fact]
    public async Task ExecuteAsync_when_useSession_persists_returned_session_on_run()
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
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(new LoopRun { Id = runId });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","prompt":"test","useSession":true,"sessionPlaceholder":"research"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        loopRunStore.Verify(
            s => s.UpsertSessionBindingAsync(runId, "Capturing", "research", "new-session-from-adapter"),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_when_useSession_is_disabled_does_not_persist_returned_session()
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
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(new LoopRun { Id = runId });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","prompt":"test"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        loopRunStore.Verify(
            s => s.UpsertSessionBindingAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_when_useSession_binds_placeholder_to_returned_session()
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
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(new LoopRun { Id = runId });
        loopRunStore.Setup(s => s.GetSessionBindingAsync(runId, "Capturing", "research"))
            .ReturnsAsync((LoopRunSessionBinding?)null);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","prompt":"test","useSession":true,"sessionPlaceholder":"research"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.Null(testAdapter.CapturedSessionId);
        loopRunStore.Verify(
            s => s.UpsertSessionBindingAsync(runId, "Capturing", "research", "new-session-from-adapter"),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_useSession_round_trips_so_second_execution_resolves_first_session()
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
            .ReturnsAsync(Array.Empty<LoopRunNode>());
        loopRunStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(new LoopRun { Id = runId });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);
        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","prompt":"test","useSession":true,"sessionPlaceholder":"research"}""";
        NodeExecutionContext MakeCtx() => new(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        // First run: no incoming session, adapter returns "new-session-from-adapter".
        var first = await executor.ExecuteAsync(MakeCtx());
        Assert.True(first.Success);
        Assert.Null(testAdapter.CapturedSessionId);
        loopRunStore.Verify(
            s => s.UpsertSessionBindingAsync(runId, "Capturing", "research", "new-session-from-adapter"),
            Times.Once);

        // Second run: must observe the session we just wrote.
        loopRunStore.Setup(s => s.GetSessionBindingAsync(runId, "Capturing", "research"))
            .ReturnsAsync(new LoopRunSessionBinding
            {
                LoopRunId = runId,
                AdapterName = "Capturing",
                PlaceholderId = "research",
                SessionId = "new-session-from-adapter"
            });
        var second = await executor.ExecuteAsync(MakeCtx());
        Assert.True(second.Success);
        Assert.Equal("new-session-from-adapter", testAdapter.CapturedSessionId);
        Assert.Equal("new-session-from-adapter", testAdapter.CapturedIncomingSessionId);
    }

    [Fact]
    public async Task ExecuteAsync_when_useSession_is_enabled_without_placeholder_fails()
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
            .Returns(() => new CapturingAdapter());

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(It.IsAny<Guid>()))
            .ReturnsAsync(Array.Empty<LoopRunNode>());
        loopRunStore.Setup(s => s.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new LoopRun { Id = Guid.NewGuid() });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);
        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","prompt":"test","useSession":true}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = Guid.NewGuid(), Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.False(result.Success);
        Assert.Contains("sessionPlaceholder", result.Error);
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

        var config = """{"aiProviderId":"my-provider","prompt":"test prompt","adapterConfig":{"temperature":0.5,"maxTokens":8192}}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.True(result.Success);
        Assert.NotNull(testAdapter.CapturedAdapterConfig);
        Assert.Equal(0.5, Convert.ToDouble(testAdapter.CapturedAdapterConfig!["temperature"]));
        Assert.Equal(8192, Convert.ToInt32(testAdapter.CapturedAdapterConfig!["maxTokens"]));
    }

    private sealed class CapturingAdapter : IAgentAdapter
    {
        public int CapturedCount;
        public string? CapturedPrompt;
        public Dictionary<string, object?>? CapturedAdapterConfig;
        public string? CapturedSessionId;
        public string? CapturedIncomingSessionId;

        public string Name => "Capturing";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
        {
            CapturedCount = ctx.ExecutionCount;
            CapturedPrompt = ctx.Prompt;
            CapturedAdapterConfig = ctx.AdapterConfig;
            CapturedSessionId = ctx.SessionId;
            CapturedIncomingSessionId = ctx.IncomingSessionId;
            return Task.FromResult(NodeExecutionResult.Ok("ok", sessionId: "new-session-from-adapter", incomingSessionId: ctx.IncomingSessionId));
        }
    }

    private sealed class RejectingAdapter : IAgentAdapter
    {
        public string Name => "Rejecting";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
            => Task.FromResult(NodeExecutionResult.Ok("I cannot complete this task"));
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

        services.AddSingleton<IAiProviderConcurrencyTracker, AiProviderConcurrencyTracker>();

        return services.BuildServiceProvider();
    }

    private static NodeExecutionContext BuildNodeExecutionContext(string? providerName)
        => BuildNodeExecutionContextWithRetry(providerName, retryCount: 0);

    private static NodeExecutionContext BuildNodeExecutionContextWithRetry(string? providerName, int retryCount)
    {
        var config = providerName != null
            ? $"{{\"aiProviderId\":\"{providerName}\",\"prompt\":\"test prompt\"}}"
            : "{\"prompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = retryCount },
            Node: new LoopNode { Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }

    private static NodeExecutionContext BuildNodeExecutionContextWithIds(string? providerName, Guid runId, Guid nodeId)
        => BuildNodeExecutionContextWithIdsAndRetry(providerName, runId, nodeId, retryCount: 0);

    private static NodeExecutionContext BuildNodeExecutionContextWithIdsAndRetry(string? providerName, Guid runId, Guid nodeId, int retryCount)
    {
        var config = providerName != null
            ? $"{{\"aiProviderId\":\"{providerName}\",\"prompt\":\"test prompt\"}}"
            : "{\"prompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = retryCount },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }
}
