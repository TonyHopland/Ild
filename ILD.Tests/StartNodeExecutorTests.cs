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
        var workItemId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        var worktreePath = Path.Combine(Path.GetTempPath(), $"ild-test-{workItemId:N}");
        var basePath = Path.Combine(Path.GetTempPath(), $"ild-base-{repoId:N}");

        try
        {
            Directory.CreateDirectory(worktreePath);
            Directory.CreateDirectory(Path.Combine(basePath, ".git"));

            var workItem = new WorkItem
            {
                Id = workItemId,
                RepositoryId = repoId,
                Title = "test",
                Description = "test",
                WorktreePath = null,
                BranchName = null,
            };

            var repository = new Repository
            {
                Id = repoId,
                Name = "test-repo",
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };

            var workItemStore = new Mock<IWorkItemStore>();
            workItemStore.Setup(s => s.GetByIdAsync(workItemId))
                .ReturnsAsync(workItem)
                .Callback(() =>
                {
                    // After first call, worktree path is still null so Start node will enter creation path
                });
            workItemStore.Setup(s => s.UpdateAsync(It.IsAny<WorkItem>()))
                .Returns(Task.CompletedTask);

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);

            var sp = BuildServiceProvider(workItemStore.Object, providerStore.Object, repoManager.Object);

            var executor = new StartNodeExecutor(repoManager.Object, sp);

            var ctx = new NodeExecutionContext(
                Run: new ILD.Data.Entities.LoopRun { Id = Guid.NewGuid() },
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
        var workItemId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        var worktreePath = Path.Combine(Path.GetTempPath(), $"ild-test-{workItemId:N}");
        var basePath = Path.Combine(Path.GetTempPath(), $"ild-base-{repoId:N}");

        try
        {
            Directory.CreateDirectory(worktreePath);
            Directory.CreateDirectory(Path.Combine(basePath, ".git"));

            var workItem = new WorkItem
            {
                Id = workItemId,
                RepositoryId = repoId,
                Title = "test",
                Description = "test",
                WorktreePath = null,
                BranchName = null,
            };

            var repository = new Repository
            {
                Id = repoId,
                Name = "test-repo",
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };

            var workItemStore = new Mock<IWorkItemStore>();
            workItemStore.Setup(s => s.GetByIdAsync(workItemId))
                .ReturnsAsync(workItem);
            workItemStore.Setup(s => s.UpdateAsync(It.IsAny<WorkItem>()))
                .Returns(Task.CompletedTask);

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.PullAsync(basePath, CancellationToken.None))
                .ReturnsAsync(false);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);

            var sp = BuildServiceProvider(workItemStore.Object, providerStore.Object, repoManager.Object);

            var executor = new StartNodeExecutor(repoManager.Object, sp);

            var ctx = new NodeExecutionContext(
                Run: new ILD.Data.Entities.LoopRun { Id = Guid.NewGuid() },
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
        IWorkItemStore workItemStore,
        IProviderStore providerStore,
        IRepositoryManager repoManager)
    {
        var services = new ServiceCollection();
        services.AddSingleton(workItemStore);
        services.AddSingleton(providerStore);
        services.AddSingleton(repoManager);
        return services.BuildServiceProvider();
    }
}
