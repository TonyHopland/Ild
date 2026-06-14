using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class StartNodeExecutorTests : IDisposable
{
    private readonly string _baseRepo;

    public StartNodeExecutorTests()
    {
        // A directory containing a ".git" folder so EnsureWorktreeAsync treats it
        // as an existing base repo (skips the clone path and runs fetch + reset).
        _baseRepo = Path.Combine(Path.GetTempPath(), "ild-start-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_baseRepo, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseRepo, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private (Mock<IRepositoryManager> RepoManager, IServiceProvider Services, LoopRun Run, LoopNode Node) BuildContext(
        Mock<IRepositoryManager> repoManager,
        Mock<IWorktreePreviewService>? preview = null)
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository
        {
            Id = repoId,
            Name = "r",
            CloneUrl = "https://example.com/o/r.git",
            DefaultBranch = "main",
            WorktreesPath = _baseRepo,
            RemoteProviderId = Guid.NewGuid(),
        };

        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(repoManager.Object);
        if (preview is not null)
            services.AddSingleton(preview.Object);
        var sp = services.BuildServiceProvider();

        var node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.Start, Config = "{}" };
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" };
        return (repoManager, sp, run, node);
    }

    [Fact]
    public async Task When_base_repo_fetch_fails_node_fails_and_no_worktree_is_created()
    {
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.FetchAsync(_baseRepo, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(false);

        var (mgr, sp, run, node) = BuildContext(repoManager);

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("fetch origin", fail.Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WorktreeReady);
        // A stale base must never be turned into a worktree.
        mgr.Verify(m => m.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        mgr.Verify(m => m.ResetHardAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task When_fetch_succeeds_worktree_is_prepared_and_node_succeeds()
    {
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.ResetHardAsync(_baseRepo, "origin/main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.CreateWorktreeAsync(_baseRepo, It.IsAny<string>()))
            .ReturnsAsync("/tmp/worktree");
        repoManager.Setup(m => m.RebaseAsync("/tmp/worktree", "origin/main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (mgr, sp, run, node) = BuildContext(repoManager);

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WorktreeReady);
        Assert.Contains(outcomes, o => o is NodeOutcome.Success);
        // The base repo must be fetched before it is reset to the latest origin tip.
        mgr.Verify(m => m.FetchAsync(_baseRepo, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Once);
        mgr.Verify(m => m.ResetHardAsync(_baseRepo, "origin/main", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A repo manager that prepares a worktree at <c>/tmp/worktree</c> on the happy path.</summary>
    private Mock<IRepositoryManager> HappyRepoManager()
    {
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.ResetHardAsync(_baseRepo, "origin/main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.CreateWorktreeAsync(_baseRepo, It.IsAny<string>()))
            .ReturnsAsync("/tmp/worktree");
        repoManager.Setup(m => m.RebaseAsync("/tmp/worktree", "origin/main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return repoManager;
    }

    [Fact]
    public async Task When_run_install_requested_install_runs_in_worktree_and_node_succeeds()
    {
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var (_, sp, run, node) = BuildContext(HappyRepoManager(), preview);
        node.Config = "{\"runInstall\":true}";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.Success);
        // Install must run against the freshly prepared worktree, not the base repo.
        preview.Verify(p => p.InstallAsync("/tmp/worktree", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_run_install_requested_and_install_fails_node_fails()
    {
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("install step exited non-zero"));

        var (_, sp, run, node) = BuildContext(HappyRepoManager(), preview);
        node.Config = "{\"runInstall\":true}";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("install step exited non-zero", fail.Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Success);
    }

    [Fact]
    public async Task When_run_install_not_requested_install_is_skipped()
    {
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default Start config — runInstall absent — must not touch the preview service.
        var (_, sp, run, node) = BuildContext(HappyRepoManager(), preview);

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.Contains(outcomes, o => o is NodeOutcome.Success);
        preview.Verify(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
