using FluentAssertions;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
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
    public async Task ExecuteAsync_stops_preview_then_destroys_worktree_and_clears_WorktreePath()
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

        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(worktreePath);
        try
        {
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = version.Id,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.Running,
                RepositoryId = repoEntity.Id,
                WorktreePath = worktreePath,
            };
            db.Context.LoopRuns.Add(run);
            db.Context.SaveChanges();

            var repo = new Mock<IRepositoryManager>();
            repo.Setup(r => r.DestroyWorktreeAsync(worktreePath)).Returns(Task.CompletedTask);

            var preview = new Mock<IWorktreePreviewService>();
            preview.Setup(p => p.StopAsync(worktreePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WorktreePreviewResponse { State = "stopped", Configured = false });

            var services = new ServiceCollection();
            services.AddSingleton((ILoopRunStore)db.LoopRuns);
            services.AddSingleton((IProviderStore)db.Providers);
            services.AddSingleton(preview.Object);
            var sp = services.BuildServiceProvider();

            var executor = new CleanupNodeExecutor(repo.Object, sp);
            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
            var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
            var wi = new WorkItemView { Id = run.WorkItemId, Title = "wi" };
            var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("destroyed");
            result.Output.Should().Contain(worktreePath);
            preview.Verify(p => p.StopAsync(worktreePath, It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(r => r.DestroyWorktreeAsync(worktreePath), Times.Once);
            var reloaded = db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == run.Id);
            reloaded.WorktreePath.Should().BeNull("Cleanup must clear the WorktreePath after destroying the worktree");
        }
        finally
        {
            if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_gracefully_handles_missing_preview_service()
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

        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(worktreePath);
        try
        {
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = version.Id,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.Running,
                RepositoryId = repoEntity.Id,
                WorktreePath = worktreePath,
            };
            db.Context.LoopRuns.Add(run);
            db.Context.SaveChanges();

            var repo = new Mock<IRepositoryManager>();
            repo.Setup(r => r.DestroyWorktreeAsync(worktreePath)).Returns(Task.CompletedTask);

            // No preview service registered — should still work
            var services = new ServiceCollection();
            services.AddSingleton((ILoopRunStore)db.LoopRuns);
            services.AddSingleton((IProviderStore)db.Providers);
            var sp = services.BuildServiceProvider();

            var executor = new CleanupNodeExecutor(repo.Object, sp);
            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
            var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
            var wi = new WorkItemView { Id = run.WorkItemId, Title = "wi" };
            var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("destroyed");
            repo.Verify(r => r.DestroyWorktreeAsync(worktreePath), Times.Once);
        }
        finally
        {
            if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, true);
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

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.Running,
            RepositoryId = repoEntity.Id,
            WorktreePath = null,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();

        var repo = new Mock<IRepositoryManager>();
        var services = new ServiceCollection();
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton<IProviderStore>(db.Providers);
        var sp = services.BuildServiceProvider();

        var executor = new CleanupNodeExecutor(repo.Object, sp);
        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
        var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
        var wi = new WorkItemView { Id = run.WorkItemId, Title = "wi" };
        var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

        var result = await executor.ExecuteAsync(ctx);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("skipped");
        repo.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_reports_failure_when_destroy_fails()
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

        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(worktreePath);
        try
        {
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = version.Id,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.Running,
                RepositoryId = repoEntity.Id,
                WorktreePath = worktreePath,
            };
            db.Context.LoopRuns.Add(run);
            db.Context.SaveChanges();

            var repo = new Mock<IRepositoryManager>();
            repo.Setup(r => r.DestroyWorktreeAsync(worktreePath)).ThrowsAsync(new InvalidOperationException("disk full"));

            var preview = new Mock<IWorktreePreviewService>();
            preview.Setup(p => p.StopAsync(worktreePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WorktreePreviewResponse { State = "stopped", Configured = false });

            var services = new ServiceCollection();
            services.AddSingleton((ILoopRunStore)db.LoopRuns);
            services.AddSingleton((IProviderStore)db.Providers);
            services.AddSingleton(preview.Object);
            var sp = services.BuildServiceProvider();

            var executor = new CleanupNodeExecutor(repo.Object, sp);
            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
            var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
            var wi = new WorkItemView { Id = run.WorkItemId, Title = "wi" };
            var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeFalse("cleanup failure must not be reported as success");
            result.Error.Should().Contain("failed");
            result.Error.Should().Contain("disk full");
            var reloaded = db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == run.Id);
            reloaded.WorktreePath.Should().Be(worktreePath, "WorktreePath must be preserved so the worktree can be recovered or cleaned up on retry");
        }
        finally
        {
            if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_reports_failure_when_preview_stop_fails()
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

        var worktreePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(worktreePath);
        try
        {
            var run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = version.Id,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
                Status = LoopRunStatus.Running,
                RepositoryId = repoEntity.Id,
                WorktreePath = worktreePath,
            };
            db.Context.LoopRuns.Add(run);
            db.Context.SaveChanges();

            var repo = new Mock<IRepositoryManager>();

            var preview = new Mock<IWorktreePreviewService>();
            preview.Setup(p => p.StopAsync(worktreePath, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("preview stop failed"));

            var services = new ServiceCollection();
            services.AddSingleton((ILoopRunStore)db.LoopRuns);
            services.AddSingleton((IProviderStore)db.Providers);
            services.AddSingleton(preview.Object);
            var sp = services.BuildServiceProvider();

            var executor = new CleanupNodeExecutor(repo.Object, sp);
            var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Cleanup, Label = "cleanup" };
            var runNode = new LoopRunNode { Id = Guid.NewGuid(), LoopRunId = run.Id, LoopNodeId = node.Id };
            var wi = new WorkItemView { Id = run.WorkItemId, Title = "wi" };
            var ctx = new NodeExecutionContext(run, runNode, node, wi, null, default);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeFalse("cleanup failure must not be reported as success");
            result.Error.Should().Contain("failed");
            result.Error.Should().Contain("preview stop failed");
            repo.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never, "worktree destroy should not run if preview stop fails");
            var reloaded = db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == run.Id);
            reloaded.WorktreePath.Should().Be(worktreePath, "WorktreePath must be preserved so the worktree can be recovered or cleaned up on retry");
        }
        finally
        {
            if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, true);
        }
    }
}
