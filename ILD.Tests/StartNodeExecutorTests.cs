using FluentAssertions;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class StartNodeExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_runs_pull_on_existing_base_repo_before_worktree_creation()
    {
        var workItemId = Guid.NewGuid().ToString();
        var repoId = Guid.NewGuid();
        var worktreePath = Path.Combine(Path.GetTempPath(), $"ild-test-{workItemId:N}");
        var basePath = Path.Combine(Path.GetTempPath(), $"ild-base-{repoId:N}");

        try
        {
            Directory.CreateDirectory(worktreePath);
            Directory.CreateDirectory(Path.Combine(basePath, ".git"));

            var workItem = new WorkItemView
            {
                Id = workItemId,
                RepositoryId = repoId,
                Title = "test",
                Description = "test",
            };

            var repository = new Repository
            {
                Id = repoId,
                Name = "test-repo",
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);

            var runId = Guid.NewGuid();
            var loopRunStore = new Mock<ILoopRunStore>();
            loopRunStore.Setup(s => s.GetByIdAsync(runId))
                .ReturnsAsync(new ILD.Data.Entities.LoopRun { Id = runId, WorktreePath = null });

            var sp = BuildServiceProvider(providerStore.Object, repoManager.Object, loopRunStore.Object);

            var executor = new StartNodeExecutor(repoManager.Object, sp);

            var ctx = new NodeExecutionContext(
                Run: new ILD.Data.Entities.LoopRun { Id = runId },
                RunNode: new ILD.Data.Entities.LoopRunNode { RetryCount = 0 },
                Node: new ILD.Data.Entities.LoopNode { Id = Guid.NewGuid() },
                WorkItem: workItem,
                PreviousNodeOutput: null,
                CancellationToken: CancellationToken.None);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
            // Pull was called on the base repo before worktree creation
            repoManager.Verify(r => r.PullAsync(basePath, CancellationToken.None), Times.Once);
            repoManager.Verify(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()), Times.Once);
        }
        finally
        {
            try { Directory.Delete(worktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_pull_failure_does_not_fail_node()
    {
        var workItemId = Guid.NewGuid().ToString();
        var repoId = Guid.NewGuid();
        var worktreePath = Path.Combine(Path.GetTempPath(), $"ild-test-{workItemId:N}");
        var basePath = Path.Combine(Path.GetTempPath(), $"ild-base-{repoId:N}");

        try
        {
            Directory.CreateDirectory(worktreePath);
            Directory.CreateDirectory(Path.Combine(basePath, ".git"));

            var workItem = new WorkItemView
            {
                Id = workItemId,
                RepositoryId = repoId,
                Title = "test",
                Description = "test",
            };

            var repository = new Repository
            {
                Id = repoId,
                Name = "test-repo",
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.PullAsync(basePath, CancellationToken.None))
                .ReturnsAsync(false);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);

            var runId = Guid.NewGuid();
            var loopRunStore = new Mock<ILoopRunStore>();
            loopRunStore.Setup(s => s.GetByIdAsync(runId))
                .ReturnsAsync(new ILD.Data.Entities.LoopRun { Id = runId, WorktreePath = null });

            var sp = BuildServiceProvider(providerStore.Object, repoManager.Object, loopRunStore.Object);

            var executor = new StartNodeExecutor(repoManager.Object, sp);

            var ctx = new NodeExecutionContext(
                Run: new ILD.Data.Entities.LoopRun { Id = runId },
                RunNode: new ILD.Data.Entities.LoopRunNode { RetryCount = 0 },
                Node: new ILD.Data.Entities.LoopNode { Id = Guid.NewGuid() },
                WorkItem: workItem,
                PreviousNodeOutput: null,
                CancellationToken: CancellationToken.None);

            var result = await executor.ExecuteAsync(ctx);

            result.Success.Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(worktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    private static IServiceProvider BuildServiceProvider(
        IProviderStore providerStore,
        IRepositoryManager repoManager,
        ILoopRunStore? loopRunStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(providerStore);
        services.AddSingleton(repoManager);
        if (loopRunStore != null)
            services.AddSingleton(loopRunStore);
        return services.BuildServiceProvider();
    }
}
