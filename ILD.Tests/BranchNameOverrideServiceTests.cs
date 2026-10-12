using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The conflict check that stands between a custom branch name and a run. It
/// has to cover both sides — nothing local holding the name, and nothing on
/// origin — because either one would put the run on a branch that already has
/// history on it.
/// </summary>
public class BranchNameOverrideServiceTests : IDisposable
{
    /// <summary>
    /// Temp base-clone directories stood up by <see cref="Setup"/>, removed when
    /// the test class is torn down — otherwise every run leaves one behind.
    /// </summary>
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best effort — a leftover temp dir must not fail a test */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task An_illegal_name_is_a_validation_error_not_a_conflict()
    {
        var (svc, _, _, _) = Setup();

        var verdict = await svc.InspectAsync("feature foo", null, "WI-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(verdict.ValidationError);
        Assert.Null(verdict.Conflict);
        Assert.False(verdict.IsUsable);
    }

    [Fact]
    public async Task A_free_name_is_usable()
    {
        var (svc, db, repoId, git) = Setup();
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        git.Setup(g => g.RemoteHasBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(false);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(verdict.IsUsable);
        Assert.False(verdict.ExistsOnOrigin);
        _ = db;
    }

    [Fact]
    public async Task A_run_of_the_same_work_item_holding_the_branch_is_a_conflict_naming_that_run()
    {
        var (svc, db, repoId, _) = Setup();
        var run = SeedRun(db, "WI-1", "feature/foo", LoopRunStatus.Completed);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(verdict.ValidationError);
        Assert.Contains("`feature/foo`", verdict.Conflict);
        Assert.Contains("already used locally", verdict.Conflict);
        Assert.Contains(run.Id.ToString(), verdict.Conflict);
        Assert.Contains("this work item", verdict.Conflict);
    }

    /// <summary>
    /// Attaching only waives origin's claim on the name. A run row still means
    /// a worktree of that name may exist under the shared root, so it blocks an
    /// attach exactly as it blocks a fresh branch — and origin is still asked,
    /// because the dialog offers the attach checkbox off that answer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_run_holding_the_name_is_a_conflict_even_when_attaching_and_origin_is_still_reported(bool attach)
    {
        var (svc, db, repoId, git) = Setup();
        SeedRun(db, "WI-1", "feature/foo", LoopRunStatus.Completed);
        git.Setup(g => g.RemoteHasBranchAsync(It.IsAny<string>(), "feature/foo", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken,
            attachToExistingBranch: attach);

        Assert.Contains("already used locally", verdict.Conflict);
        Assert.True(verdict.ExistsOnOrigin);
    }

    /// <summary>
    /// Both ways of freeing the branch refuse a run that has not finished, so
    /// the advice has to depend on the holder's status — pointing someone at
    /// Clean up or Delete while the holder is live sends them to a 400.
    /// </summary>
    [Theory]
    [InlineData(LoopRunStatus.Running)]
    [InlineData(LoopRunStatus.WaitingHuman)]
    public async Task An_active_holder_is_told_to_end_the_run_before_freeing_the_branch(LoopRunStatus status)
    {
        var (svc, db, repoId, _) = Setup();
        SeedRun(db, "WI-1", "feature/foo", status);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("still active", verdict.Conflict);
        Assert.Contains("cancel it", verdict.Conflict);
    }

    [Theory]
    [InlineData(LoopRunStatus.Completed)]
    [InlineData(LoopRunStatus.Failed)]
    [InlineData(LoopRunStatus.Cancelled)]
    public async Task A_finished_holder_is_offered_cleanup_as_the_non_destructive_way_out(LoopRunStatus status)
    {
        var (svc, db, repoId, _) = Setup();
        SeedRun(db, "WI-1", "feature/foo", status);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Clean up that run", verdict.Conflict);
        Assert.Contains("keeping its history", verdict.Conflict);
        Assert.DoesNotContain("still active", verdict.Conflict);
    }

    [Fact]
    public async Task A_run_of_another_work_item_holding_the_branch_names_that_work_item()
    {
        var (svc, db, repoId, _) = Setup();
        SeedRun(db, "WI-9", "feature/foo", LoopRunStatus.Running);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("work item WI-9", verdict.Conflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_local_branch_with_no_run_behind_it_is_still_a_conflict(bool attach)
    {
        var (svc, _, repoId, git) = Setup(baseRepoOnDisk: true);
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), "feature/foo")).ReturnsAsync(true);
        git.Setup(g => g.RemoteHasBranchAsync(It.IsAny<string>(), "feature/foo", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken,
            attachToExistingBranch: attach);

        Assert.Contains("already exists locally", verdict.Conflict);
        Assert.True(verdict.ExistsOnOrigin);
    }

    [Fact]
    public async Task A_branch_on_origin_is_a_conflict_naming_origin()
    {
        var (svc, _, repoId, git) = Setup();
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        git.Setup(g => g.RemoteHasBranchAsync("https://example/repo.git", "feature/foo", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("`feature/foo` already exists on origin", verdict.Conflict);
        Assert.True(verdict.ExistsOnOrigin);
    }

    [Fact]
    public async Task Attaching_to_a_branch_on_origin_is_not_a_conflict()
    {
        var (svc, _, repoId, git) = Setup(baseRepoOnDisk: true);
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        git.Setup(g => g.RemoteHasBranchAsync("https://example/repo.git", "feature/foo", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken,
            attachToExistingBranch: true);

        Assert.True(verdict.IsUsable);
        Assert.True(verdict.ExistsOnOrigin);
    }

    [Fact]
    public async Task Attaching_to_a_branch_missing_from_origin_is_a_conflict_saying_so()
    {
        // A fresh branch would otherwise be cut from the base and pushed under a
        // name the human believes is someone else's existing work.
        var (svc, _, repoId, git) = Setup(baseRepoOnDisk: true);
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        git.Setup(g => g.RemoteHasBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(false);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken,
            attachToExistingBranch: true);

        Assert.Null(verdict.ValidationError);
        Assert.Contains("`feature/foo`", verdict.Conflict);
        Assert.Contains("does not exist on origin", verdict.Conflict);
        Assert.False(verdict.ExistsOnOrigin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unreachable_origin_is_not_a_conflict_for_a_fresh_branch_but_cannot_confirm_an_attach(bool attach)
    {
        // Refusing to start a fresh branch over an unanswered question would
        // park work items on a flaky network; the Start node fails the run on
        // an unreachable origin anyway. An attach needs the answer: nothing
        // else confirms there is a branch to attach to.
        var (svc, _, repoId, git) = Setup();
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        git.Setup(g => g.RemoteHasBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((bool?)null);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1", cancellationToken: TestContext.Current.CancellationToken,
            attachToExistingBranch: attach);

        Assert.Equal(!attach, verdict.IsUsable);
        if (attach)
            Assert.Contains("Could not reach origin to confirm that branch `feature/foo` exists", verdict.Conflict);
        Assert.Null(verdict.ExistsOnOrigin);
    }

    private static LoopRun SeedRun(TestDb db, string workItemId, string branchName, LoopRunStatus status)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = status,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            BranchName = branchName,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    /// <param name="baseRepoOnDisk">
    /// Point the repository's worktrees path at a real directory holding a
    /// <c>.git</c>, so the local-branch lookup is reached at all. Without it the
    /// base clone simply isn't there yet, which is not a conflict.
    /// </param>
    private (BranchNameOverrideService svc, TestDb db, Guid repoId, Mock<IRepositoryManager> git) Setup(
        bool baseRepoOnDisk = false)
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        string? worktreesPath = null;
        if (baseRepoOnDisk)
        {
            worktreesPath = Path.Combine(Path.GetTempPath(), $"ild-branch-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(worktreesPath, ".git"));
            _tempDirs.Add(worktreesPath);
        }
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            RemoteProviderId = remote.Id,
            CloneUrl = "https://example/repo.git",
            WorktreesPath = worktreesPath,
        };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();

        var git = new Mock<IRepositoryManager>();
        var svc = new BranchNameOverrideService(db.LoopRuns, db.Providers, git.Object);
        return (svc, db, repo.Id, git);
    }
}
