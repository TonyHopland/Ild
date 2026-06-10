using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

public class RunReclaimerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public async Task Reclaim_destroys_worktree_then_deletes_branch_via_base_repo()
    {
        var worktree = NewTempDir();
        var repo = new Mock<IRepositoryManager>();
        repo.Setup(r => r.ResolveBaseRepoPathAsync(worktree)).ReturnsAsync("/repos/x");
        repo.Setup(r => r.DestroyWorktreeAsync(worktree))
            .Callback(() => Directory.Delete(worktree, recursive: true))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.LocalBranchExistsAsync("/repos/x", "ild/wi-a-run-1")).ReturnsAsync(true);
        repo.Setup(r => r.DeleteLocalBranchAsync("/repos/x", "ild/wi-a-run-1")).ReturnsAsync(true);

        var ok = await Build(repo).ReclaimLocalStateAsync(Run(worktree, "ild/wi-a-run-1"));

        Assert.True(ok);
        repo.Verify(r => r.DestroyWorktreeAsync(worktree), Times.Once);
        repo.Verify(r => r.PruneWorktreesAsync("/repos/x"), Times.Once);
        repo.Verify(r => r.DeleteLocalBranchAsync("/repos/x", "ild/wi-a-run-1"), Times.Once);
    }

    [Fact]
    public async Task Reclaim_reports_failure_when_worktree_survives_destroy()
    {
        var worktree = NewTempDir();
        var repo = new Mock<IRepositoryManager>(); // DestroyWorktreeAsync is a no-op

        var ok = await Build(repo).ReclaimLocalStateAsync(Run(worktree, "ild/wi-a-run-1"));

        Assert.False(ok);
        // Branch deletion must not run while the worktree still holds the branch.
        repo.Verify(r => r.DeleteLocalBranchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Reclaim_falls_back_to_repository_path_when_worktree_already_gone()
    {
        // Simulates CleanupToDone (worktree destroyed, branch left) or a manually
        // deleted worktree: the branch must still be found through the run's
        // Repository instead of leaking forever.
        var baseRepo = NewTempDir();
        Directory.CreateDirectory(Path.Combine(baseRepo, ".git"));
        var repositoryId = Guid.NewGuid();

        var providers = new Mock<IProviderStore>();
        providers.Setup(p => p.GetRepositoryByIdAsync(repositoryId))
            .ReturnsAsync(new Repository { Id = repositoryId, Name = "r", CloneUrl = "https://x/r.git", WorktreesPath = baseRepo });

        var repo = new Mock<IRepositoryManager>();
        repo.Setup(r => r.LocalBranchExistsAsync(baseRepo, "ild/wi-a-run-1")).ReturnsAsync(true);
        repo.Setup(r => r.DeleteLocalBranchAsync(baseRepo, "ild/wi-a-run-1")).ReturnsAsync(true);

        var run = Run(worktree: "/nonexistent/worktree", branch: "ild/wi-a-run-1");
        run.RepositoryId = repositoryId;

        var ok = await new RunReclaimer(repo.Object, providers.Object).ReclaimLocalStateAsync(run);

        Assert.True(ok);
        repo.Verify(r => r.PruneWorktreesAsync(baseRepo), Times.Once);
        repo.Verify(r => r.DeleteLocalBranchAsync(baseRepo, "ild/wi-a-run-1"), Times.Once);
    }

    [Fact]
    public async Task Reclaim_succeeds_when_branch_is_unreachable()
    {
        // No worktree, no repository row: the branch cannot be located, but the
        // run row must not be held hostage forever.
        var repo = new Mock<IRepositoryManager>();
        var run = Run(worktree: null, branch: "ild/wi-a-run-1");
        run.RepositoryId = null;

        Assert.True(await Build(repo).ReclaimLocalStateAsync(run));
    }

    [Fact]
    public async Task Reclaim_reports_failure_when_branch_delete_fails()
    {
        var worktree = NewTempDir();
        var repo = new Mock<IRepositoryManager>();
        repo.Setup(r => r.ResolveBaseRepoPathAsync(worktree)).ReturnsAsync("/repos/x");
        repo.Setup(r => r.DestroyWorktreeAsync(worktree))
            .Callback(() => Directory.Delete(worktree, recursive: true))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.LocalBranchExistsAsync("/repos/x", "ild/wi-a-run-1")).ReturnsAsync(true);
        repo.Setup(r => r.DeleteLocalBranchAsync("/repos/x", "ild/wi-a-run-1")).ReturnsAsync(false);

        Assert.False(await Build(repo).ReclaimLocalStateAsync(Run(worktree, "ild/wi-a-run-1")));
    }

    [Fact]
    public async Task Reclaim_skips_branch_step_when_branch_already_absent()
    {
        var worktree = NewTempDir();
        var repo = new Mock<IRepositoryManager>();
        repo.Setup(r => r.ResolveBaseRepoPathAsync(worktree)).ReturnsAsync("/repos/x");
        repo.Setup(r => r.DestroyWorktreeAsync(worktree))
            .Callback(() => Directory.Delete(worktree, recursive: true))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.LocalBranchExistsAsync("/repos/x", "ild/wi-a-run-1")).ReturnsAsync(false);

        Assert.True(await Build(repo).ReclaimLocalStateAsync(Run(worktree, "ild/wi-a-run-1")));
        repo.Verify(r => r.DeleteLocalBranchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static RunReclaimer Build(Mock<IRepositoryManager> repo)
        => new(repo.Object, new Mock<IProviderStore>().Object);

    private static LoopRun Run(string? worktree, string? branch) => new()
    {
        Id = Guid.NewGuid(),
        WorkItemId = "wi-a",
        WorktreePath = worktree,
        BranchName = branch,
    };

    private string NewTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("ild-reclaim-test-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
