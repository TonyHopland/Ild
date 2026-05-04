using FluentAssertions;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class CleanupNodeExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_clears_WorktreePath_after_destroying_worktree()
    {
        using var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repoEntity = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repoEntity);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var wi = new WorkItem
        {
            Id = Guid.NewGuid(),
            Title = "wi",
            RepositoryId = repoEntity.Id,
            LoopTemplateVersionId = version.Id,
            Status = WorkItemStatus.Running,
            WorktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        };
        Directory.CreateDirectory(wi.WorktreePath);
        try
        {
            db.Context.WorkItems.Add(wi);
            db.Context.SaveChanges();

            var repo = new Mock<IRepositoryManager>();
            repo.Setup(r => r.DestroyWorktreeAsync(wi.WorktreePath)).Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton<IWorkItemStore>(db.WorkItems);
            var sp = services.BuildServiceProvider();

            var expectedPath = wi.WorktreePath;
            var executor = new CleanupNodeExecutor(repo.Object, sp);
            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
            var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id };
            var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
            var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("destroyed");
            result.Output.Should().Contain(expectedPath);
            var reloaded = db.Fresh().WorkItems.AsNoTracking().First(w => w.Id == wi.Id);
            reloaded.WorktreePath.Should().BeNull("Cleanup must clear the WorktreePath after destroying the worktree");
        }
        finally
        {
            if (Directory.Exists(wi.WorktreePath)) Directory.Delete(wi.WorktreePath, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_reports_skipped_when_no_worktree_path()
    {
        using var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repoEntity = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repoEntity);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var wi = new WorkItem
        {
            Id = Guid.NewGuid(),
            Title = "wi",
            RepositoryId = repoEntity.Id,
            LoopTemplateVersionId = version.Id,
            Status = WorkItemStatus.Running,
            WorktreePath = null,
        };
        db.Context.WorkItems.Add(wi);
        db.Context.SaveChanges();

        var repo = new Mock<IRepositoryManager>();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkItemStore>(db.WorkItems);
        var sp = services.BuildServiceProvider();

        var executor = new CleanupNodeExecutor(repo.Object, sp);
        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id };
        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("skipped");
        repo.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_reports_failure_details_when_destroy_fails()
    {
        using var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repoEntity = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/r.git" };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repoEntity);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var wi = new WorkItem
        {
            Id = Guid.NewGuid(),
            Title = "wi",
            RepositoryId = repoEntity.Id,
            LoopTemplateVersionId = version.Id,
            Status = WorkItemStatus.Running,
            WorktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()),
        };
        Directory.CreateDirectory(wi.WorktreePath);
        try
        {
            db.Context.WorkItems.Add(wi);
            db.Context.SaveChanges();

            var expectedPath = wi.WorktreePath;
            var repo = new Mock<IRepositoryManager>();
            repo.Setup(r => r.DestroyWorktreeAsync(expectedPath)).ThrowsAsync(new InvalidOperationException("disk full"));

            var services = new ServiceCollection();
            services.AddSingleton<IWorkItemStore>(db.WorkItems);
            var sp = services.BuildServiceProvider();

            var executor = new CleanupNodeExecutor(repo.Object, sp);
            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
            var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = wi.Id };
            var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
            var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("failed");
            result.Output.Should().Contain("disk full");
            var reloaded = db.Fresh().WorkItems.AsNoTracking().First(w => w.Id == wi.Id);
            reloaded.WorktreePath.Should().BeNull("WorktreePath should still be cleared even if destroy fails");
        }
        finally
        {
            if (Directory.Exists(wi.WorktreePath)) Directory.Delete(wi.WorktreePath, true);
        }
    }
}
