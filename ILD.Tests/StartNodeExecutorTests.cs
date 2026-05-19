using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ILD.Tests;

public class StartNodeExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_runs_fetch_and_reset_hard_on_existing_base_repo_before_worktree_creation()
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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "Forgejo", Url = "https://example.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.FetchAsync(basePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.ResetHardAsync(basePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);
            repoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.RebaseAsync(worktreePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);

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

            Assert.True(result.Success);
            // Fetch + ResetHard were called on the base repo before worktree creation (replaces pull --ff-only)
            repoManager.Verify(r => r.FetchAsync(
                basePath,
                CancellationToken.None,
                It.Is<GitAuthOptions>(a => a.ApiKey == "token-123" && a.ProviderType == "Forgejo")), Times.Once);
            repoManager.Verify(r => r.ResetHardAsync(
                basePath,
                "origin/main",
                CancellationToken.None), Times.Once);
            repoManager.Verify(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()), Times.Once);
        }
        finally
        {
            try { Directory.Delete(worktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_base_repo_sync_failure_does_not_fail_node()
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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "Forgejo", Url = "https://example.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            // Fetch on base repo throws — should be swallowed (best-effort)
            repoManager.Setup(r => r.FetchAsync(basePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ThrowsAsync(new InvalidOperationException("network error"));
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);
            repoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.RebaseAsync(worktreePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);

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

            Assert.True(result.Success);
        }
        finally
        {
            try { Directory.Delete(worktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_recreates_invalid_existing_worktree()
    {
        var workItemId = Guid.NewGuid().ToString();
        var repoId = Guid.NewGuid();
        var brokenWorktreePath = Path.Combine(Path.GetTempPath(), $"ild-broken-{workItemId:N}");
        var recreatedWorktreePath = Path.Combine(Path.GetTempPath(), $"ild-recreated-{workItemId:N}");
        var basePath = Path.Combine(Path.GetTempPath(), $"ild-base-{repoId:N}");

        try
        {
            Directory.CreateDirectory(brokenWorktreePath);
            Directory.CreateDirectory(recreatedWorktreePath);
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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "Forgejo", Url = "https://example.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.ValidateWorktreeHealthAsync(brokenWorktreePath))
                .ReturnsAsync(false);
            repoManager.Setup(r => r.FetchAsync(basePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.ResetHardAsync(basePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, "ild/wi-17"))
                .ReturnsAsync(recreatedWorktreePath);
            repoManager.Setup(r => r.FetchAsync(recreatedWorktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.RebaseAsync(recreatedWorktreePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);

            var runId = Guid.NewGuid();
            var loopRunStore = new Mock<ILoopRunStore>();
            loopRunStore.Setup(s => s.GetByIdAsync(runId))
                .ReturnsAsync(new ILD.Data.Entities.LoopRun { Id = runId, WorktreePath = brokenWorktreePath, BranchName = "ild/wi-17" });

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

            Assert.True(result.Success);
            repoManager.Verify(r => r.ValidateWorktreeHealthAsync(brokenWorktreePath), Times.Once);
            repoManager.Verify(r => r.CreateWorktreeAsync(basePath, "ild/wi-17"), Times.Once);
        }
        finally
        {
            try { Directory.Delete(brokenWorktreePath, true); } catch { }
            try { Directory.Delete(recreatedWorktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_uses_configured_data_path_for_fallback_base_repo()
    {
        var workItemId = Guid.NewGuid().ToString();
        var repoId = Guid.NewGuid();
        var configuredDataPath = Path.Combine(Path.GetTempPath(), $"ild-data-{Guid.NewGuid():N}");
        var expectedBasePath = Path.Combine(configuredDataPath, "repos", repoId.ToString("N"));
        var worktreePath = Path.Combine(Path.GetTempPath(), $"ild-worktree-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(worktreePath);

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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = null,
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "GitHub", Url = "https://github.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.CloneAsync(
                    repository.CloneUrl,
                    expectedBasePath,
                    CancellationToken.None,
                    It.Is<GitAuthOptions>(a => a.ApiKey == "token-123" && a.ProviderType == "GitHub")))
                .ReturnsAsync((true, null));
            repoManager.Setup(r => r.CreateWorktreeAsync(expectedBasePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);
            repoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.RebaseAsync(worktreePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);

            var runId = Guid.NewGuid();
            var loopRunStore = new Mock<ILoopRunStore>();
            loopRunStore.Setup(s => s.GetByIdAsync(runId))
                .ReturnsAsync(new ILD.Data.Entities.LoopRun { Id = runId, WorktreePath = null });

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:DataPath"] = configuredDataPath,
                })
                .Build();

            var sp = BuildServiceProvider(providerStore.Object, repoManager.Object, loopRunStore.Object, config);

            var executor = new StartNodeExecutor(repoManager.Object, sp);

            var ctx = new NodeExecutionContext(
                Run: new ILD.Data.Entities.LoopRun { Id = runId },
                RunNode: new ILD.Data.Entities.LoopRunNode { RetryCount = 0 },
                Node: new ILD.Data.Entities.LoopNode { Id = Guid.NewGuid() },
                WorkItem: workItem,
                PreviousNodeOutput: null,
                CancellationToken: CancellationToken.None);

            var result = await executor.ExecuteAsync(ctx);

            Assert.True(result.Success);
            repoManager.Verify(r => r.CloneAsync(
                repository.CloneUrl,
                expectedBasePath,
                CancellationToken.None,
                It.IsAny<GitAuthOptions?>()), Times.Once);
            repoManager.Verify(r => r.CreateWorktreeAsync(expectedBasePath, It.IsAny<string>()), Times.Once);
        }
        finally
        {
            try { Directory.Delete(configuredDataPath, true); } catch { }
            try { Directory.Delete(worktreePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_fetches_and_rebases_after_worktree_creation()
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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "Forgejo", Url = "https://example.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.FetchAsync(basePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.ResetHardAsync(basePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);
            repoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.RebaseAsync(worktreePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);

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

            Assert.True(result.Success);
            repoManager.Verify(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()), Times.Once);
            repoManager.Verify(r => r.RebaseAsync(worktreePath, "origin/main", CancellationToken.None), Times.Once);
        }
        finally
        {
            try { Directory.Delete(worktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_rebase_returns_false()
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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "Forgejo", Url = "https://example.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.FetchAsync(basePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.ResetHardAsync(basePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);
            repoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            // Rebase returns false — should fail the node
            repoManager.Setup(r => r.RebaseAsync(worktreePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(false);

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

            Assert.False(result.Success);
            Assert.Contains("rebase", result.Error?.ToLowerInvariant());
            Assert.Contains("stale", result.Error?.ToLowerInvariant());
        }
        finally
        {
            try { Directory.Delete(worktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_rebase_throws()
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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "Forgejo", Url = "https://example.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.FetchAsync(basePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.ResetHardAsync(basePath, "origin/main", CancellationToken.None))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);
            repoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            // Rebase throws — should fail the node
            repoManager.Setup(r => r.RebaseAsync(worktreePath, "origin/main", CancellationToken.None))
                .ThrowsAsync(new InvalidOperationException("rebase conflict"));

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

            Assert.False(result.Success);
            Assert.Contains("rebase", result.Error?.ToLowerInvariant());
            Assert.Contains("stale", result.Error?.ToLowerInvariant());
        }
        finally
        {
            try { Directory.Delete(worktreePath, true); } catch { }
            try { Directory.Delete(basePath, true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_rebase_uses_repo_DefaultBranch()
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
                RemoteProviderId = Guid.NewGuid(),
                CloneUrl = "https://example.com/test.git",
                WorktreesPath = basePath,
                DefaultBranch = "master", // non-main default branch
            };
            var remoteProvider = new RemoteProvider { Id = repository.RemoteProviderId, Name = "provider", Type = "Forgejo", Url = "https://example.com", ApiKey = "token-123" };

            var providerStore = new Mock<IProviderStore>();
            providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId))
                .ReturnsAsync(repository);
            providerStore.Setup(s => s.GetRemoteProviderByIdAsync(repository.RemoteProviderId))
                .ReturnsAsync(remoteProvider);

            var repoManager = new Mock<IRepositoryManager>();
            repoManager.Setup(r => r.FetchAsync(basePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.ResetHardAsync(basePath, "origin/master", CancellationToken.None))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.CreateWorktreeAsync(basePath, It.IsAny<string>()))
                .ReturnsAsync(worktreePath);
            repoManager.Setup(r => r.FetchAsync(worktreePath, CancellationToken.None, It.IsAny<GitAuthOptions?>()))
                .ReturnsAsync(true);
            repoManager.Setup(r => r.RebaseAsync(worktreePath, "origin/master", CancellationToken.None))
                .ReturnsAsync(true);

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

            Assert.True(result.Success);
            // ResetHard and Rebase use origin/master from repo.DefaultBranch, not hardcoded origin/main
            repoManager.Verify(r => r.ResetHardAsync(basePath, "origin/master", CancellationToken.None), Times.Once);
            repoManager.Verify(r => r.RebaseAsync(worktreePath, "origin/master", CancellationToken.None), Times.Once);
            repoManager.Verify(r => r.ResetHardAsync(basePath, "origin/main", It.IsAny<CancellationToken>()), Times.Never);
            repoManager.Verify(r => r.RebaseAsync(worktreePath, "origin/main", It.IsAny<CancellationToken>()), Times.Never);
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
        ILoopRunStore? loopRunStore = null,
        IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(providerStore);
        services.AddSingleton(repoManager);
        if (loopRunStore != null)
            services.AddSingleton(loopRunStore);
        if (configuration != null)
            services.AddSingleton(configuration);
        return services.BuildServiceProvider();
    }
}
