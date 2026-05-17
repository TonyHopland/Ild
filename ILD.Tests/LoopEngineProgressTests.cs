using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests;

public class LoopEngineProgressTests
{
    [Fact]
    public async Task Node_progress_callback_broadcasts_via_NodeProgressAsync()
    {
        // Arrange: build engine with a mock notifier that captures NodeProgressAsync calls
        using var db = new TestDb();
        var notifier = new Mock<IRunNotifier>();
        var progressLines = new System.Collections.Concurrent.ConcurrentBag<string>();
        notifier.Setup(n => n.NodeProgressAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .Callback<Guid, Guid, string>((_, _, line) => progressLines.Add(line))
            .Returns(Task.CompletedTask);

        var fakes = new Dictionary<NodeType, FakeExecutor>();
        foreach (var type in Enum.GetValues<NodeType>())
            fakes[type] = new FakeExecutor(type);

        var registry = new NodeExecutorRegistry(fakes.Values);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INodeExecutorRegistry>(registry);
        services.AddSingleton<IRunNotifier>(notifier.Object);
        services.AddSingleton<LoopEngine>();
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton<ILoopTemplateStore>(db.LoopTemplates);
        services.AddSingleton<IProviderStore>(db.Providers);
        services.AddSingleton<IEventLogStore>(db.EventLogs);
        services.AddSingleton<IEventLogService>(new EventLogService(db.EventLogs, db.LoopRuns));
        services.AddSingleton<IAuthStore>(db.Auth);
        services.AddSingleton<IRepositoryManager>(new Mock<IRepositoryManager>().Object);
        services.AddSingleton(db.ServerClient);
        services.AddSingleton(db.ServerOptions);
        services.AddSingleton<IWorkItemManager>(sp => new WorkItemManager(
            sp.GetRequiredService<IRepositoryManager>(),
            sp.GetRequiredService<IProviderStore>(),
            sp.GetRequiredService<IEventLogService>(),
            sp.GetRequiredService<ILoopRunStore>(),
            sp.GetRequiredService<ILD.Core.Services.Remote.IWorkItemServerClient>(),
            sp.GetRequiredService<ILD.Core.Services.Remote.IWorkItemServerOptionsResolver>()));

        var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<LoopEngine>();

        // Build a simple graph: Start -> AI -> Cleanup
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);

        var startNode = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Start, Label = "start" };
        var aiNode = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.AI, Label = "ai" };
        var cleanupNode = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Cleanup, Label = "cleanup" };
        db.Context.LoopNodes.Add(startNode);
        db.Context.LoopNodes.Add(aiNode);
        db.Context.LoopNodes.Add(cleanupNode);

        var aiProvider = new AiProvider { Id = Guid.NewGuid(), Name = "test-ai", Type = "openai", BaseUrl = "http://localhost", ApiKey = "key", Model = "gpt-4" };
        db.Context.AiProviders.Add(aiProvider);

        db.Context.LoopNodeEdges.Add(new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = startNode.Id, TargetNodeId = aiNode.Id, EdgeType = EdgeType.OnSuccess });
        db.Context.LoopNodeEdges.Add(new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = aiNode.Id, TargetNodeId = cleanupNode.Id, EdgeType = EdgeType.OnSuccess });

        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        // Mirror onto the WorkItemServer fake so the now server-first
        // WorkItemManager can resolve the item.
        db.Server.ServerDb.WorkItems.Add(new ILD.WorkItemServer.Domain.WorkItem
        {
            Id = workItemId,
            Title = "wi",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Status = ILD.WorkItemServer.Domain.WorkItemStatus.Running,
        });
        db.Server.ServerDb.SaveChanges();

        // The AI fake executor calls the progress callback with streaming text
        fakes[NodeType.AI].AsyncBehavior = async ctx =>
        {
            if (ctx.ProgressCallback != null)
            {
                await ctx.ProgressCallback("thinking...");
                await ctx.ProgressCallback("analyzing code...");
                await ctx.ProgressCallback("done");
            }
            return NodeExecutionResult.Ok("ai output");
        };

        // Act
        await engine.RunAsync(run.Id);

        // Assert: notifier received the progress lines
        Assert.Contains("thinking...", progressLines);
        Assert.Contains("analyzing code...", progressLines);
        Assert.Contains("done", progressLines);

        // Verify the notifier was called with the correct runId and nodeId
        notifier.Verify(n => n.NodeProgressAsync(run.Id, aiNode.Id, "thinking..."), Times.Once);
        notifier.Verify(n => n.NodeProgressAsync(run.Id, aiNode.Id, "analyzing code..."), Times.Once);
        notifier.Verify(n => n.NodeProgressAsync(run.Id, aiNode.Id, "done"), Times.Once);
    }
}
