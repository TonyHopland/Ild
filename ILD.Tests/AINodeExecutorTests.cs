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
    public async Task ExecuteAsync_fails_when_no_provider_key_set()
    {
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync())
            .ReturnsAsync((AiProvider?)null);

        var registry = new Mock<IAgentAdapterRegistry>();

        var sp = BuildServiceProvider(providerStore.Object, registry.Object);

        var executor = new AINodeExecutor(sp);

        var result = await executor.ExecuteAsync(BuildNodeExecutionContext(null));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No AI provider found");
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

        result.Success.Should().BeTrue();
        result.Output.Should().Be("adapter-result");
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

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No AI provider found");
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
        var config = $"{{\"aiProviderId\":\"my-provider\",\"initialPrompt\":\"first prompt here\"}}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
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

        var config = "{\"aiProviderId\":\"my-provider\",\"initialPrompt\":\"test prompt\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Config = config, TimeoutSeconds = 1 },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
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

        var config = "{\"aiProviderId\":\"my-provider\",\"initialPrompt\":\"test prompt\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Config = config, TimeoutSeconds = 300 },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
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

        var config = "{\"aiProviderId\":\"my-provider\",\"initialPrompt\":\"test\",\"rejectPattern\":\"I cannot|I'm unable\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("AI rejected");
        result.Output.Should().Be("I cannot complete this task");
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

        var config = "{\"aiProviderId\":\"my-provider\",\"initialPrompt\":\"test\",\"rejectPattern\":\"REJECT\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("adapter-result");
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

        var config = "{\"aiProviderId\":\"my-provider\",\"initialPrompt\":\"test\",\"rejectPattern\":\"cannot\"}";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_passes_incoming_session_to_adapter_when_sessionInput_is_incoming()
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

        var existingRun = new LoopRun
        {
            Id = runId,
            SessionsJson = "[{\"providerId\":\"" + providerId + "\",\"sessionId\":\"existing-session-123\"}]"
        };
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(existingRun);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test","sessionInput":"incoming"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedSessionId.Should().Be("existing-session-123");
        testAdapter.CapturedIncomingSessionId.Should().Be("existing-session-123");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_pass_session_to_adapter_when_sessionInput_is_new()
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

        var existingRun = new LoopRun
        {
            Id = runId,
            SessionsJson = "[{\"providerId\":\"" + providerId + "\",\"sessionId\":\"existing-session-123\"}]"
        };
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(existingRun);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test","sessionInput":"new"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedSessionId.Should().BeNull();
        testAdapter.CapturedIncomingSessionId.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_stores_adapter_session_on_run_when_sessionOutput_is_current()
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

        var run = new LoopRun { Id = runId };
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(run);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test","sessionOutput":"current"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        run.SessionsJson.Should().Contain(providerId.ToString());
        run.SessionsJson.Should().Contain("new-session-from-adapter");
    }

    [Fact]
    public async Task ExecuteAsync_removes_session_from_run_when_sessionOutput_is_none()
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

        var run = new LoopRun
        {
            Id = runId,
            SessionsJson = "[{\"providerId\":\"" + providerId + "\",\"sessionId\":\"old-session\"}]"
        };
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(run);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test","sessionOutput":"none"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        run.SessionsJson.Should().NotContain(providerId.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_restores_incoming_session_on_run_when_sessionOutput_is_incoming()
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

        var run = new LoopRun
        {
            Id = runId,
            SessionsJson = "[{\"providerId\":\"" + providerId + "\",\"sessionId\":\"original-session\"}]"
        };
        loopRunStore.Setup(s => s.GetByIdAsync(runId))
            .ReturnsAsync(run);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);

        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test","sessionInput":"incoming","sessionOutput":"incoming"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedSessionId.Should().Be("original-session");
        run.SessionsJson.Should().Contain("original-session");
        run.SessionsJson.Should().NotContain("new-session-from-adapter");
    }

    [Fact]
    public async Task ExecuteAsync_session_round_trips_so_second_execution_resolves_first_session()
    {
        // Regression: the writer once produced PascalCase JSON
        // ({"ProviderId":...}) while the reader looked for camelCase keys
        // (providerId/sessionId). Sessions written by the executor were
        // therefore invisible on the next execution and the AI node would
        // always start a fresh session.
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

        var run = new LoopRun { Id = runId };
        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());
        loopRunStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(run);
        // Capture writes back into the local run instance so the second
        // call observes the persisted SessionsJson.
        loopRunStore.Setup(s => s.UpdateRunAsync(It.IsAny<LoopRun>()))
            .Returns<LoopRun>(r => { run.SessionsJson = r.SessionsJson; return Task.CompletedTask; });

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);
        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test","sessionInput":"incoming","sessionOutput":"current"}""";
        NodeExecutionContext MakeCtx() => new(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        // First run: no incoming session, adapter returns "new-session-from-adapter".
        var first = await executor.ExecuteAsync(MakeCtx());
        first.Success.Should().BeTrue();
        testAdapter.CapturedSessionId.Should().BeNull("first run has no session yet");
        run.SessionsJson.Should().Contain("new-session-from-adapter");

        // Second run: must observe the session we just wrote.
        var second = await executor.ExecuteAsync(MakeCtx());
        second.Success.Should().BeTrue();
        testAdapter.CapturedSessionId.Should().Be("new-session-from-adapter",
            "the session written on the first execution must be readable on the next");
        testAdapter.CapturedIncomingSessionId.Should().Be("new-session-from-adapter");
    }

    [Fact]
    public async Task ExecuteAsync_resolves_legacy_pascal_case_sessions_json()
    {
        // Defensive: SessionsJson rows written before the case fix used
        // PascalCase keys. The resolver must continue to read those so
        // existing runs don't lose their session after upgrade.
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

        var run = new LoopRun
        {
            Id = runId,
            SessionsJson = "[{\"ProviderId\":\"" + providerId + "\",\"SessionId\":\"legacy-session\"}]"
        };
        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());
        loopRunStore.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(run);

        var sp = BuildServiceProvider(providerStore.Object, registry.Object, loopRunStore.Object);
        var executor = new AINodeExecutor(sp);

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test","sessionInput":"incoming"}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);
        result.Success.Should().BeTrue();
        testAdapter.CapturedSessionId.Should().Be("legacy-session");
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

        var config = """{"aiProviderId":"my-provider","initialPrompt":"test prompt","adapterConfig":{"temperature":0.5,"maxTokens":8192}}""";
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
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
        public string? CapturedSessionId;
        public string? CapturedIncomingSessionId;

        public string Name => "Capturing";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
        {
            CapturedCount = ctx.ExecutionCount;
            CapturedInitialPrompt = ctx.InitialPrompt;
            CapturedLoopPrompt = ctx.LoopPrompt;
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

        return services.BuildServiceProvider();
    }

    private static NodeExecutionContext BuildNodeExecutionContext(string? providerName)
        => BuildNodeExecutionContextWithRetry(providerName, retryCount: 0);

    private static NodeExecutionContext BuildNodeExecutionContextWithRetry(string? providerName, int retryCount)
    {
        var config = providerName != null
            ? $"{{\"aiProviderId\":\"{providerName}\",\"initialPrompt\":\"test prompt\"}}"
            : "{\"initialPrompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = retryCount },
            Node: new LoopNode { Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }

    private static NodeExecutionContext BuildNodeExecutionContextWithIds(string? providerName, Guid runId, Guid nodeId)
        => BuildNodeExecutionContextWithIdsAndRetry(providerName, runId, nodeId, retryCount: 0);

    private static NodeExecutionContext BuildNodeExecutionContextWithIdsAndRetry(string? providerName, Guid runId, Guid nodeId, int retryCount)
    {
        var config = providerName != null
            ? $"{{\"aiProviderId\":\"{providerName}\",\"initialPrompt\":\"test prompt\"}}"
            : "{\"initialPrompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = retryCount },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }
}
