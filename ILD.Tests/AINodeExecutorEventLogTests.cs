using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Text.Json;

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

        Assert.True(result.Success);
        Assert.NotNull(testAdapter.CapturedContext);
        Assert.Equal(2, testAdapter.CapturedContext!.EventLogSummary.Count());
        Assert.Contains(testAdapter.CapturedContext.EventLogSummary, e => e.Contains("NodeStarted"));
        Assert.Contains(testAdapter.CapturedContext.EventLogSummary, e => e.Contains("NodeCompleted"));

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

        Assert.True(result.Success);
        Assert.NotNull(testAdapter.CapturedContext);
        Assert.Empty(testAdapter.CapturedContext!.EventLogSummary);
    }

    [Fact]
    public void DescribeInput_only_reports_prompt_and_session_settings()
    {
        var providerStore = new Mock<IProviderStore>();
        var registry = new Mock<IAgentAdapterRegistry>();
        var loopRunStore = new Mock<ILoopRunStore>();
        var eventLogService = new Mock<IEventLogService>();

        var sp = BuildServiceProvider(
            providerStore.Object,
            registry.Object,
            loopRunStore.Object,
            eventLogService.Object);

        var executor = new AINodeExecutor(sp);
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = Guid.NewGuid() },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode
            {
                Id = Guid.NewGuid(),
                Config = "{\"prompt\":\"{{PreviousNode.Output}}\",\"useSession\":true,\"sessionPlaceholder\":\"plan\"}",
            },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "title", Description = "desc" },
            PreviousNodeOutput: "prompt output",
            CancellationToken: CancellationToken.None);

        using var doc = JsonDocument.Parse(executor.DescribeInput(ctx));
        Assert.Equal("{{PreviousNode.Output}}", doc.RootElement.GetProperty("prompt").GetString());
        Assert.True(doc.RootElement.GetProperty("useSession").GetBoolean());
        Assert.Equal("plan", doc.RootElement.GetProperty("sessionPlaceholder").GetString());
        Assert.False(doc.RootElement.TryGetProperty("context", out _));
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
        services.AddSingleton<IAiProviderConcurrencyTracker, AiProviderConcurrencyTracker>();
        return services.BuildServiceProvider();
    }

    private static NodeExecutionContext BuildNodeExecutionContext(string? providerName, Guid runId, Guid nodeId)
    {
        var config = providerName != null
            ? $"{{\"aiProviderId\":\"{providerName}\",\"prompt\":\"test prompt\"}}"
            : "{\"prompt\":\"test prompt\"}";

        return new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { RetryCount = 0 },
            Node: new LoopNode { Id = nodeId, Config = config },
            WorkItem: new WorkItemView { Id = Guid.NewGuid().ToString(), Title = "test", Description = "desc" },
            PreviousNodeOutput: null,
            CancellationToken: CancellationToken.None);
    }
}
