using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class PRNodeExecutorTests
{
    [Fact]
    public async Task When_PR_exists_and_PrCommentTemplate_is_set_posts_rendered_comment()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView
        {
            Id = "WI-1",
            Title = "Title",
            Description = "Body",
            RepositoryId = repoId,
        };
        var repo = new Repository
        {
            Id = repoId,
            Name = "repo",
            CloneUrl = "https://example.com/owner/repo.git",
            DefaultBranch = "main",
            RemoteProviderId = Guid.NewGuid(),
        };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);

        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestCommentAsync("https://example.com/owner/repo.git", "42", It.IsAny<string>()))
            .ReturnsAsync(true);

        var rendering = new Mock<IPromptRenderingService>();
        rendering.Setup(r => r.RenderAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<WorkItemView>(), It.IsAny<string?>()))
            .ReturnsAsync((string template, Guid _, WorkItemView _, string? _) => template.Replace("{{WorkItem.Title}}", "Title"));

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(rendering.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            NodeType = NodeType.PR,
            Config = """{"prCommentTemplate":"Update on {{WorkItem.Title}}"}""",
        };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            PrUrl = "https://example.com/owner/repo/pull/42",
        };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        remote.Verify(r => r.CreatePullRequestCommentAsync(
            "https://example.com/owner/repo.git", "42", "Update on Title"), Times.Once);
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.PrCreated);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task When_PR_exists_without_PrCommentTemplate_skips_comment_and_parks()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", PrUrl = "https://example.com/o/r/pull/7" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        // Strict mock — verifies the remote was never invoked at all.
        Assert.Contains(outcomes, o => o is NodeOutcome.WaitingAction);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
    }

    [Fact]
    public async Task When_PR_exists_and_comment_post_fails_node_fails()
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository { Id = repoId, Name = "r", CloneUrl = "https://example.com/o/r.git", DefaultBranch = "main", RemoteProviderId = Guid.NewGuid() };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.CreatePullRequestCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = """{"prCommentTemplate":"hi"}""" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1", PrUrl = "https://example.com/o/r/pull/9" };

        var executor = new PRNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Contains("PR comment failed", fail.Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WaitingAction);
    }
}
