using FluentAssertions;
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

public class AINodeExecutorEventLogTests
{
    private sealed class CapturingEventLogAdapter : IAgentAdapter
    {
        public LoopRunContext? CapturedContext;

        public string Name => "Capturing";
        public string[] SupportedProviderTypes => ["test"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
        {
            CapturedContext = ctx.RunContext;
            return Task.FromResult(NodeExecutionResult.Ok("ok"));
        }
    }

    [Fact]
    public async Task ExecuteAsync_populates_event_log_summary_from_event_log_service()
    {
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model",
        };

        var testAdapter = new CapturingEventLogAdapter();

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => testAdapter);

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        var eventLogService = new Mock<IEventLogService>();
        eventLogService.Setup(s => s.GetByRunIdAsync(runId))
            .ReturnsAsync(new[]
            {
                new EventLogEntry(runId, "NodeStarted", "AI node started"),
                new EventLogEntry(runId, "NodeCompleted", "Cmd node succeeded"),
            });

        var sp = BuildServiceProvider(
            providerStore.Object,
            registry.Object,
            loopRunStore.Object,
            eventLogService.Object);

        var executor = new AINodeExecutor(sp);

        var ctx = BuildNodeExecutionContext("my-provider", runId, nodeId);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedContext.Should().NotBeNull();
        testAdapter.CapturedContext!.EventLogSummary.Should().HaveCount(2);
        testAdapter.CapturedContext.EventLogSummary.Should().Contain(e => e.Contains("NodeStarted"));
        testAdapter.CapturedContext.EventLogSummary.Should().Contain(e => e.Contains("NodeCompleted"));

        // Verify the event log service was actually called
        eventLogService.Verify(s => s.GetByRunIdAsync(runId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_passes_empty_event_log_when_no_entries()
    {
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "my-provider",
            Type = "test",
            BaseUrl = "https://test.api",
            Model = "test-model",
        };

        var testAdapter = new CapturingEventLogAdapter();

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetAiProviderByNameAsync(It.IsAny<string>()))
            .ReturnsAsync(provider);

        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.ResolveForProvider(It.IsAny<AiProvider>()))
            .Returns(() => testAdapter);

        var loopRunStore = new Mock<ILoopRunStore>();
        loopRunStore.Setup(s => s.GetRunNodesAsync(runId))
            .ReturnsAsync(Array.Empty<LoopRunNode>());

        var eventLogService = new Mock<IEventLogService>();
        eventLogService.Setup(s => s.GetByRunIdAsync(runId))
            .ReturnsAsync(Array.Empty<EventLogEntry>());

        var sp = BuildServiceProvider(
            providerStore.Object,
            registry.Object,
            loopRunStore.Object,
            eventLogService.Object);

        var executor = new AINodeExecutor(sp);

        var ctx = BuildNodeExecutionContext("my-provider", runId, nodeId);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        testAdapter.CapturedContext.Should().NotBeNull();
        testAdapter.CapturedContext!.EventLogSummary.Should().BeEmpty();
    }

    private static IServiceProvider BuildServiceProvider(
        IProviderStore providerStore,
        IAgentAdapterRegistry registry,
        ILoopRunStore loopRunStore,
        IEventLogService eventLogService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(providerStore);
        services.AddSingleton(registry);
        services.AddSingleton(loopRunStore);
        services.AddSingleton(eventLogService);
        return services.BuildServiceProvider();
    }

    private static NodeExecutionContext BuildNodeExecutionContext(string? providerName, Guid runId, Guid nodeId)
    {
        var config = providerName != null
            ? $"{{\"aiProviderId\":\"{providerName}\",\"initialPrompt\":\"test prompt\"}}"
            : "{\"initialPrompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }
}
