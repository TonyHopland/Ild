using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class StartNodeExecutorTests : IDisposable
{
    private readonly string _baseRepo;
    private readonly string _dataPath;

    public StartNodeExecutorTests()
    {
        // A directory containing a ".git" folder so EnsureWorktreeAsync treats it
        // as an existing base repo (skips the clone path and runs fetch + reset).
        _baseRepo = Path.Combine(Path.GetTempPath(), "ild-start-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_baseRepo, ".git"));
        // Where the clone-on-demand path lands when a repository has no
        // WorktreesPath. Owned here so those tests don't write into the test
        // runner's working directory.
        _dataPath = Path.Combine(Path.GetTempPath(), "ild-start-data-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseRepo, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private (Mock<IRepositoryManager> RepoManager, IServiceProvider Services, LoopRun Run, LoopNode Node) BuildContext(
        Mock<IRepositoryManager> repoManager,
        Mock<IWorktreePreviewService>? preview = null,
        string? previewEnv = null,
        string? worktreesPath = null)
    {
        var repoId = Guid.NewGuid();
        var workItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        var repo = new Repository
        {
            Id = repoId,
            Name = "r",
            CloneUrl = "https://example.com/o/r.git",
            DefaultBranch = "main",
            // Blank sends the executor down the clone-on-demand path.
            WorktreesPath = worktreesPath ?? _baseRepo,
            RemoteProviderId = Guid.NewGuid(),
            PreviewEnv = previewEnv,
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
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:DataPath"] = _dataPath })
            .Build());
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
            .ReturnsAsync(new RebaseResult(true, Array.Empty<string>(), null));

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
            .ReturnsAsync(new RebaseResult(true, Array.Empty<string>(), null));
        return repoManager;
    }

    [Fact]
    public async Task A_pinned_base_branch_is_used_for_reset_worktree_and_rebase_alike()
    {
        // The base has to reach every step that touches it. Resetting the base
        // repo to origin/release/1.0 but rebasing onto origin/main would hand the
        // run a worktree nobody asked for.
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.RemoteBranchExistsAsync(_baseRepo, "release/1.0")).ReturnsAsync(true);
        repoManager.Setup(m => m.ResetHardAsync(_baseRepo, "origin/release/1.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.CreateWorktreeAsync(_baseRepo, It.IsAny<string>()))
            .ReturnsAsync("/tmp/worktree");
        repoManager.Setup(m => m.RebaseAsync("/tmp/worktree", "origin/release/1.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RebaseResult(true, Array.Empty<string>(), null));

        var (mgr, sp, run, node) = BuildContext(repoManager);
        run.BaseBranchOverride = "release/1.0";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WorktreeReady);
        mgr.Verify(m => m.ResetHardAsync(_baseRepo, "origin/release/1.0", It.IsAny<CancellationToken>()), Times.Once);
        mgr.Verify(m => m.RebaseAsync("/tmp/worktree", "origin/release/1.0", It.IsAny<CancellationToken>()), Times.Once);
        // The repository default must not leak into any of the three.
        mgr.Verify(m => m.ResetHardAsync(It.IsAny<string>(), "origin/main", It.IsAny<CancellationToken>()), Times.Never);
        mgr.Verify(m => m.RebaseAsync(It.IsAny<string>(), "origin/main", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_base_branch_missing_from_origin_fails_the_node_instead_of_falling_back()
    {
        // Silently starting from main would build the run on history the human
        // did not ask for, and the PR would target the wrong branch too.
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.RemoteBranchExistsAsync(_baseRepo, "no/such/branch")).ReturnsAsync(false);

        var (mgr, sp, run, node) = BuildContext(repoManager);
        run.BaseBranchOverride = "no/such/branch";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Equal(EdgeType.OnFailure, fail.Edge);
        Assert.Contains("no/such/branch", fail.Reason);
        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.WorktreeReady);
        // Nothing is reset, built, or rebased on a base we could not find.
        mgr.Verify(m => m.ResetHardAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mgr.Verify(m => m.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_freshly_cloned_base_repo_is_still_moved_onto_the_override_before_the_worktree()
    {
        // A clone lands on the remote's default branch. Skipping the reset
        // because "we just cloned it, it must be current" leaves HEAD on that
        // default, and `git worktree add -b` takes no start point — so the run
        // would branch from the default and the following rebase would replay
        // its commits onto the base. Currency was never the point of the reset;
        // landing on the right ref is.
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.CloneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((true, (string?)null));
        repoManager.Setup(m => m.FetchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.RemoteBranchExistsAsync(It.IsAny<string>(), "release/1.0")).ReturnsAsync(true);
        repoManager.Setup(m => m.ResetHardAsync(It.IsAny<string>(), "origin/release/1.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoManager.Setup(m => m.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("/tmp/worktree");
        repoManager.Setup(m => m.RebaseAsync("/tmp/worktree", "origin/release/1.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RebaseResult(true, Array.Empty<string>(), null));

        // No WorktreesPath and no .git anywhere → the clone-on-demand path.
        var (mgr, sp, run, node) = BuildContext(repoManager, worktreesPath: string.Empty);
        run.BaseBranchOverride = "release/1.0";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.WorktreeReady);
        mgr.Verify(m => m.CloneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Once);
        // The reset is the whole point: it must happen on the cloned path too,
        // and it must happen before the worktree is cut from HEAD.
        mgr.Verify(m => m.ResetHardAsync(It.IsAny<string>(), "origin/release/1.0", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_freshly_cloned_base_repo_with_a_missing_override_fails_before_anything_is_built()
    {
        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.CloneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((true, (string?)null));
        repoManager.Setup(m => m.RemoteBranchExistsAsync(It.IsAny<string>(), "no/such/branch")).ReturnsAsync(false);

        var (mgr, sp, run, node) = BuildContext(repoManager, worktreesPath: string.Empty);
        run.BaseBranchOverride = "no/such/branch";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        var fail = outcomes.OfType<NodeOutcome.Fail>().Single();
        Assert.Contains("no/such/branch", fail.Reason);
        mgr.Verify(m => m.ResetHardAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mgr.Verify(m => m.CreateWorktreeAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Without_an_override_the_base_is_the_repository_default_and_is_not_probed()
    {
        // The repository's own default branch is discovered from the remote
        // rather than typed, so it costs a git call nobody needs to re-check.
        var (mgr, sp, run, node) = BuildContext(HappyRepoManager());

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        mgr.Verify(m => m.ResetHardAsync(_baseRepo, "origin/main", It.IsAny<CancellationToken>()), Times.Once);
        mgr.Verify(m => m.RemoteBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task When_run_install_requested_install_runs_in_worktree_and_node_succeeds()
    {
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreeInstallResult(true));

        var (_, sp, run, node) = BuildContext(HappyRepoManager(), preview);
        node.Config = "{\"runInstall\":true}";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        Assert.Contains(outcomes, o => o is NodeOutcome.Success);
        // Install must run against the freshly prepared worktree, not the base repo.
        preview.Verify(p => p.InstallAsync("/tmp/worktree", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_run_install_requested_repo_custom_env_is_threaded_to_install()
    {
        // The repository's custom .env must reach the install step so install
        // scripts see the same secrets the services will.
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreeInstallResult(true));

        const string envText = "API_TOKEN=secret\n# comment\nDB_URL=postgres://x";
        var (_, sp, run, node) = BuildContext(HappyRepoManager(), preview, previewEnv: envText);
        node.Config = "{\"runInstall\":true}";

        var executor = new StartNodeExecutor();
        await foreach (var _ in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
        {
        }

        preview.Verify(p => p.InstallAsync("/tmp/worktree", null, envText, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_run_install_requested_but_no_ild_config_present_node_warns_and_succeeds()
    {
        // A project without an ild.config.json preview profile must not fail the
        // run — the install is skipped best-effort and the reason is surfaced as a
        // warning on the node output.
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreeInstallResult(false, "No ild.config.json found in worktree root."));

        var (_, sp, run, node) = BuildContext(HappyRepoManager(), preview);
        node.Config = "{\"runInstall\":true}";

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.DoesNotContain(outcomes, o => o is NodeOutcome.Fail);
        var success = outcomes.OfType<NodeOutcome.Success>().Single();
        Assert.Contains("ild.config install skipped", success.Output);
        Assert.Contains("No ild.config.json found in worktree root.", success.Output);
    }

    [Fact]
    public async Task When_run_install_requested_and_install_fails_node_fails()
    {
        var preview = new Mock<IWorktreePreviewService>();
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        preview.Setup(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorktreeInstallResult(true));

        // Default Start config — runInstall absent — must not touch the preview service.
        var (_, sp, run, node) = BuildContext(HappyRepoManager(), preview);

        var executor = new StartNodeExecutor();
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);

        Assert.Contains(outcomes, o => o is NodeOutcome.Success);
        preview.Verify(p => p.InstallAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
