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
public class BranchNameOverrideServiceTests
{
    [Fact]
    public async Task An_illegal_name_is_a_validation_error_not_a_conflict()
    {
        var (svc, _, _, _) = Setup();

        var verdict = await svc.InspectAsync("feature foo", null, "WI-1");

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

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1");

        Assert.True(verdict.IsUsable);
        _ = db;
    }

    [Fact]
    public async Task A_run_of_the_same_work_item_holding_the_branch_is_a_conflict_naming_that_run()
    {
        var (svc, db, repoId, git) = Setup();
        var run = SeedRun(db, "WI-1", "feature/foo", LoopRunStatus.Completed);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1");

        Assert.Null(verdict.ValidationError);
        Assert.Contains("`feature/foo`", verdict.Conflict);
        Assert.Contains("already used locally", verdict.Conflict);
        Assert.Contains(run.Id.ToString(), verdict.Conflict);
        Assert.Contains("this work item", verdict.Conflict);
        // The remote is never consulted once a local holder is found.
        git.Verify(g => g.RemoteHasBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);
    }

    [Fact]
    public async Task A_run_of_another_work_item_holding_the_branch_names_that_work_item()
    {
        var (svc, db, repoId, _) = Setup();
        SeedRun(db, "WI-9", "feature/foo", LoopRunStatus.Running);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1");

        Assert.Contains("work item WI-9", verdict.Conflict);
    }

    [Fact]
    public async Task A_local_branch_with_no_run_behind_it_is_still_a_conflict()
    {
        var (svc, _, repoId, git) = Setup(baseRepoOnDisk: true);
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), "feature/foo")).ReturnsAsync(true);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1");

        Assert.Contains("already exists locally", verdict.Conflict);
    }

    [Fact]
    public async Task A_branch_on_origin_is_a_conflict_naming_origin()
    {
        var (svc, _, repoId, git) = Setup();
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        git.Setup(g => g.RemoteHasBranchAsync("https://example/repo.git", "feature/foo", It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync(true);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1");

        Assert.Contains("`feature/foo` already exists on origin", verdict.Conflict);
    }

    [Fact]
    public async Task An_unreachable_origin_is_not_treated_as_a_conflict()
    {
        // Refusing to start over an unanswered question would park work items
        // on a flaky network; the Start node fails the run on an unreachable
        // origin anyway.
        var (svc, _, repoId, git) = Setup();
        git.Setup(g => g.LocalBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        git.Setup(g => g.RemoteHasBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .ReturnsAsync((bool?)null);

        var verdict = await svc.InspectAsync("feature/foo", repoId, "WI-1");

        Assert.True(verdict.IsUsable);
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
    private static (BranchNameOverrideService svc, TestDb db, Guid repoId, Mock<IRepositoryManager> git) Setup(
        bool baseRepoOnDisk = false)
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        string? worktreesPath = null;
        if (baseRepoOnDisk)
        {
            worktreesPath = Path.Combine(Path.GetTempPath(), $"ild-branch-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(worktreesPath, ".git"));
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
