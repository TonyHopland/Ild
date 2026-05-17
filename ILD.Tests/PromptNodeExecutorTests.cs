using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class PromptNodeExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_renders_prompt_template_and_returns_output()
    {
        var services = new ServiceCollection();
        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(s => s.GetByRunIdAsync(It.IsAny<Guid>(), It.IsAny<int?>()))
            .Returns(Task.FromResult<IEnumerable<EventLogEntry>>(Array.Empty<EventLogEntry>()));

        services.AddSingleton<IPromptTemplateResolver, PromptTemplateResolver>();
        services.AddScoped<IPromptRenderingService, PromptRenderingService>();
        services.AddSingleton<IEventLogService>(eventLog.Object);
        var sp = services.BuildServiceProvider();

        var executor = new PromptNodeExecutor(sp);
        var runId = Guid.NewGuid();
        var ctx = new NodeExecutionContext(
            Run: new LoopRun { Id = runId },
            RunNode: new LoopRunNode { Id = Guid.NewGuid(), RetryCount = 0 },
            Node: new LoopNode
            {
                Id = Guid.NewGuid(),
                Config = "{\"prompt\":\"Title: {{WorkItem.Title}}\\nPrevious: {{PreviousNode.Output}}\"}"
            },
            WorkItem: new WorkItemView
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Plan work",
                Description = "desc",
                WorktreePath = "/tmp",
                BranchName = "main"
            },
            PreviousNodeOutput: "Draft a task breakdown",
            CancellationToken: CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        Assert.IsType<NodeOutcome.Succeeded>(result);
        Assert.True(result.Success);
        Assert.Equal("Title: Plan work\nPrevious: Draft a task breakdown", result.Output);
        Assert.Equal("Title: Plan work\nPrevious: Draft a task breakdown", ((NodeOutcome.Succeeded)result).ResolvedPrompt);
    }
}