using FluentAssertions;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class PRNodeExecutorTests
{
    [Fact]
    public async Task creates_PR_when_PrUrl_is_null()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var wi = new WorkItem { Id = Guid.NewGuid(), Title = "wi", RepositoryId = repo.Id, LoopTemplateVersionId = version.Id, Status = WorkItemStatus.Running, BranchName = "ild/test" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.WorkItems.Add(wi);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var prUrl = "https://gitea.example/r/pull/1";

        var mockRemote = new Mock<IRemoteProvider>();
        mockRemote.Setup(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult(prUrl, prUrl, RemotePrStatus.Open, null));

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(db.WorkItems);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        var refreshed = db.Fresh().WorkItems.First(w => w.Id == wi.Id);
        refreshed.PrUrl.Should().Be(prUrl);
    }

    [Fact]
    public async Task reuses_existing_PR_when_PrUrl_is_set()
    {
        using var db = new TestDb();

        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://gitea.example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://gitea.example/r.git", DefaultBranch = "main" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume, MaxNodeExecutions = 200, MaxWallClockHours = 24 };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var existingPrUrl = "https://gitea.example/r/pull/42";
        var wi = new WorkItem { Id = Guid.NewGuid(), Title = "wi", RepositoryId = repo.Id, LoopTemplateVersionId = version.Id, Status = WorkItemStatus.Running, BranchName = "ild/test", PrUrl = existingPrUrl };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id, LoopTemplateVersionId = version.Id, RecoveryPolicy = RecoveryPolicy.AutoResume, Status = LoopRunStatus.Running };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr" };

        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.WorkItems.Add(wi);
        db.Context.LoopRuns.Add(run);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();

        var mockRemote = new Mock<IRemoteProvider>();

        var services = new ServiceCollection();
        services.AddSingleton(mockRemote.Object);
        services.AddSingleton(db.WorkItems);
        var sp = services.BuildServiceProvider();

        var executor = new PRNodeExecutor(sp);

        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, CancellationToken.None);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Be(existingPrUrl);
        mockRemote.Verify(r => r.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
