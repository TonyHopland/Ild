using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The Files tab diffs a run's worktree against a fork point, and the fork
/// point has to be the base that worktree was actually built on. Anchored on
/// the repository default instead, an item working off a base branch reports
/// every commit that branch has diverged by as its own change.
/// </summary>
public class WorkItemsControllerDiffBaseTests
{
    [Fact]
    public async Task Files_diff_against_the_base_branch_the_run_was_built_from()
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: "release/1.0");
        using var _db = db;

        await controller.GetFiles(WorkItemId);

        repoManager.Verify(m => m.ListWorktreeFilesAsync(WorktreePath, "release/1.0", It.IsAny<WorktreeDiffRange?>()), Times.Once);
        repoManager.Verify(m => m.ListWorktreeFilesAsync(It.IsAny<string>(), "main", It.IsAny<WorktreeDiffRange?>()), Times.Never);
    }

    [Fact]
    public async Task File_content_diffs_against_the_same_base_as_the_file_list()
    {
        // Two endpoints, one fork point — a file whose diff is computed against a
        // different ref than the list it came from is worse than no diff at all.
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: "release/1.0");
        using var _db = db;

        await controller.GetFileContent(WorkItemId, "src/app.ts");

        repoManager.Verify(m => m.ReadWorktreeFileAsync(WorktreePath, "src/app.ts", "release/1.0", It.IsAny<WorktreeDiffRange?>()), Times.Once);
    }

    [Fact]
    public async Task Without_an_override_the_diff_still_anchors_on_the_repository_default()
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;

        await controller.GetFiles(WorkItemId);

        repoManager.Verify(m => m.ListWorktreeFilesAsync(WorktreePath, "main", It.IsAny<WorktreeDiffRange?>()), Times.Once);
    }

    [Fact]
    public async Task Saving_a_file_anchors_the_diff_it_answers_with_on_that_same_base()
    {
        // The save hands back the file as it now stands, and that response is
        // what redraws the viewer — so it has to be measured from the fork point
        // the reads use, or a save would rewrite the diff the user was reading.
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: "release/1.0");
        using var _db = db;

        var result = await controller.SaveFileContent(
            WorkItemId,
            new WorktreeFileSaveRequest { Path = "src/app.ts", Content = "edited" });

        repoManager.Verify(m => m.WriteWorktreeFileAsync(WorktreePath, "src/app.ts", "edited", "release/1.0", It.IsAny<WorktreeDiffRange?>()), Times.Once);
        var saved = Assert.IsType<WorktreeFileContentResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("edited", saved.Content);
        Assert.Equal("modified", saved.ChangeStatus);
    }

    [Fact]
    public async Task A_save_missing_its_path_or_content_never_reaches_the_worktree()
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;

        Assert.IsType<BadRequestObjectResult>(await controller.SaveFileContent(WorkItemId, null));
        Assert.IsType<BadRequestObjectResult>(
            await controller.SaveFileContent(WorkItemId, new WorktreeFileSaveRequest { Path = " ", Content = "x" }));
        // Content absent is a malformed save; content empty is a file truncated.
        Assert.IsType<BadRequestObjectResult>(
            await controller.SaveFileContent(WorkItemId, new WorktreeFileSaveRequest { Path = "a.ts", Content = null }));
        Assert.IsType<OkObjectResult>(
            await controller.SaveFileContent(WorkItemId, new WorktreeFileSaveRequest { Path = "a.ts", Content = "" }));

        repoManager.Verify(
            m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()),
            Times.Once);
    }

    [Fact]
    public async Task A_file_is_only_editable_while_the_item_waits_on_a_human()
    {
        // Any other state and the run's agent is the one working in that
        // worktree; a save into it would be a second writer nobody can see.
        var (controller, repoManager, db, _) = await SetupAsync(
            runBaseBranchOverride: null,
            status: RemoteWorkItemStatus.Running);
        using var _db = db;

        var result = await controller.SaveFileContent(
            WorkItemId,
            new WorktreeFileSaveRequest { Path = "src/app.ts", Content = "edited" });

        Assert.IsType<ConflictObjectResult>(result);
        repoManager.Verify(
            m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()),
            Times.Never);
    }

    [Fact]
    public async Task Reading_a_file_does_not_wait_on_a_human()
    {
        // The gate is on the write alone: a run in flight is exactly when
        // someone wants to watch the files it is changing.
        var (controller, repoManager, db, _) = await SetupAsync(
            runBaseBranchOverride: null,
            status: RemoteWorkItemStatus.Running);
        using var _db = db;

        await controller.GetFiles(WorkItemId);
        await controller.GetFileContent(WorkItemId, "src/app.ts");

        repoManager.Verify(m => m.ListWorktreeFilesAsync(WorktreePath, "main", It.IsAny<WorktreeDiffRange?>()), Times.Once);
        repoManager.Verify(m => m.ReadWorktreeFileAsync(WorktreePath, "src/app.ts", "main", It.IsAny<WorktreeDiffRange?>()), Times.Once);
    }

    [Fact]
    public async Task A_save_answers_each_refusal_in_its_own_terms()
    {
        // A file that is not there reads as 404, so it has to save as one too —
        // the same path answering "not found" to one endpoint and "bad request"
        // to the other is a contract a client cannot act on. Bytes and a worktree
        // that has gone are the caller's problem, and stay 400.
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;

        Assert.IsType<NotFoundObjectResult>(await SaveRefusedWith(WorktreeFileWriteResult.NotFound));
        Assert.IsType<BadRequestObjectResult>(await SaveRefusedWith(WorktreeFileWriteResult.NotText));
        Assert.IsType<BadRequestObjectResult>(await SaveRefusedWith(WorktreeFileWriteResult.WorktreeUnavailable));

        async Task<IActionResult> SaveRefusedWith(WorktreeFileWriteResult refusal)
        {
            repoManager
                .Setup(m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()))
                .ReturnsAsync(refusal);
            return await controller.SaveFileContent(
                WorkItemId,
                new WorktreeFileSaveRequest { Path = "src/app.ts", Content = "edited" });
        }
    }

    // A run branch of two commits on Base: C1 then C2, so C2 is HEAD.
    private const string Base = "ba5eba5eba5eba5eba5eba5eba5eba5eba5eba5e";
    private const string C1 = "c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1c1";
    private const string C2 = "c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2c2";

    private static WorktreeCommitsResponse ListedCommits() => new()
    {
        BaseSha = Base,
        Commits =
        [
            new WorktreeCommit { Sha = C2, ParentSha = C1, Subject = "Second change" },
            new WorktreeCommit { Sha = C1, ParentSha = Base, Subject = "First change" },
        ],
    };

    [Fact]
    public async Task The_commit_list_is_taken_on_the_same_base_as_the_files()
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: "release/1.0");
        using var _db = db;
        var listed = ListedCommits();
        repoManager.Setup(m => m.ListWorktreeCommitsAsync(WorktreePath, "release/1.0")).ReturnsAsync(listed);

        var result = await controller.GetFileCommits(WorkItemId);

        Assert.Same(listed, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task The_commit_list_refuses_an_unknown_item_and_one_without_a_worktree()
    {
        var (controller, _, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeActionResult>(await controller.GetFileCommits("999")).StatusCode);

        var (bare, _, bareDb, _) = await SetupAsync(runBaseBranchOverride: null, withWorktree: false);
        using var _bareDb = bareDb;
        Assert.IsType<BadRequestObjectResult>(await bare.GetFileCommits(WorkItemId));
    }

    [Theory]
    [InlineData(Base, null)] // every commit plus what is not committed yet
    [InlineData(C2, null)] // only what is not committed yet
    [InlineData(C1, C2)] // the newest commit alone
    [InlineData(Base, C1)] // the oldest commit alone
    [InlineData(null, C1)] // from the base, without what came after C1
    public async Task A_range_of_listed_commits_reaches_all_three_file_endpoints(string? from, string? to)
    {
        // Listed only under the run's own base: a range checked against the
        // repository default's list would be checked against other commits.
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: "release/1.0");
        using var _db = db;
        repoManager.Setup(m => m.ListWorktreeCommitsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new WorktreeCommitsResponse { BaseSha = null, Commits = [] });
        repoManager.Setup(m => m.ListWorktreeCommitsAsync(WorktreePath, "release/1.0")).ReturnsAsync(ListedCommits());

        Assert.IsType<OkObjectResult>(await controller.GetFiles(WorkItemId, from: from, to: to));
        await controller.GetFileContent(WorkItemId, "src/app.ts", from: from, to: to);
        Assert.IsType<OkObjectResult>(await controller.SaveFileContent(
            WorkItemId, new WorktreeFileSaveRequest { Path = "src/app.ts", Content = "edited" }, from: from, to: to));

        repoManager.Verify(m => m.ListWorktreeFilesAsync(WorktreePath, "release/1.0", It.Is<WorktreeDiffRange?>(r => Asks(r, from, to))), Times.Once);
        repoManager.Verify(m => m.ReadWorktreeFileAsync(WorktreePath, "src/app.ts", "release/1.0", It.Is<WorktreeDiffRange?>(r => Asks(r, from, to))), Times.Once);
        repoManager.Verify(m => m.WriteWorktreeFileAsync(WorktreePath, "src/app.ts", "edited", "release/1.0", It.Is<WorktreeDiffRange?>(r => Asks(r, from, to))), Times.Once);
    }

    // An absent `from` may travel as null or as the base it defaults to.
    private static bool Asks(WorktreeDiffRange? range, string? from, string? to) =>
        range != null && range.To == to && (range.From == from || (from == null && range.From == Base));

    [Theory]
    [InlineData("3333333333333333333333333333333333333333", null)] // a commit not on this branch
    [InlineData("c1c1c1c", null)] // abbreviated
    [InlineData("HEAD", null)]
    [InlineData("origin/HEAD", null)]
    [InlineData("--output=/tmp/x", null)]
    [InlineData("text", null)]
    [InlineData(null, Base)] // the base is a start, never an end
    [InlineData(null, "HEAD")]
    [InlineData(Base, "c2c2c2c")]
    [InlineData(C1, "-p")]
    public async Task A_range_outside_the_listed_commits_is_refused_before_git_sees_it(string? from, string? to)
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;
        repoManager.Setup(m => m.ListWorktreeCommitsAsync(It.IsAny<string>(), It.IsAny<string?>())).ReturnsAsync(ListedCommits());

        await AssertEveryEndpointRefusesAsync(controller, repoManager, from, to);
    }

    [Theory]
    [InlineData(Base, null)]
    [InlineData(null, C1)]
    public async Task Without_a_resolved_base_every_range_is_refused(string? from, string? to)
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;
        repoManager.Setup(m => m.ListWorktreeCommitsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new WorktreeCommitsResponse { BaseSha = null, Commits = [] });

        await AssertEveryEndpointRefusesAsync(controller, repoManager, from, to);
    }

    private static async Task AssertEveryEndpointRefusesAsync(
        WorkItemsController controller, Mock<IRepositoryManager> repoManager, string? from, string? to)
    {
        Assert.IsType<BadRequestObjectResult>(await controller.GetFiles(WorkItemId, from: from, to: to));
        Assert.IsType<BadRequestObjectResult>(await controller.GetFileContent(WorkItemId, "src/app.ts", from: from, to: to));
        Assert.IsType<BadRequestObjectResult>(await controller.SaveFileContent(
            WorkItemId, new WorktreeFileSaveRequest { Path = "src/app.ts", Content = "edited" }, from: from, to: to));

        repoManager.Verify(m => m.ListWorktreeFilesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()), Times.Never);
        repoManager.Verify(m => m.ReadWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()), Times.Never);
        repoManager.Verify(m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()), Times.Never);
    }

    private const string WorkItemId = "1";
    private const string WorktreePath = "/tmp/ild-difftest-worktree";

    private static async Task<(WorkItemsController Controller, Mock<IRepositoryManager> RepoManager, TestDb Db, string Id)> SetupAsync(
        string? runBaseBranchOverride,
        RemoteWorkItemStatus status = RemoteWorkItemStatus.HumanFeedback,
        bool withWorktree = true)
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            RemoteProviderId = remote.Id,
            CloneUrl = "https://example/repo.git",
            DefaultBranch = "main",
        };
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.SaveChanges();

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(1L);

        var repoManager = new Mock<IRepositoryManager>();
        repoManager.Setup(m => m.ListWorktreeFilesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()))
            .ReturnsAsync(new List<WorktreeFileEntry>());
        repoManager.Setup(m => m.ReadWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()))
            .ReturnsAsync((WorktreeFileContentResponse?)null);
        repoManager.Setup(m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<WorktreeDiffRange?>()))
            .ReturnsAsync((string _, string path, string content, string? __, WorktreeDiffRange? ___) => WorktreeFileWriteResult.Saved(
                new WorktreeFileContentResponse { Path = path, ChangeStatus = "modified", Content = content }));

        var mgr = new WorkItemManager(
            repoManager.Object,
            db.Providers,
            eventLog.Object,
            db.LoopRuns,
            db.ServerClient,
            db.ServerOptions,
            engine: new Mock<ILoopEngine>().Object);

        var id = await mgr.CreateWorkItemAsync("t", "", repo.Id);
        // Editing is only offered while the item waits on a human, so that is
        // the state these save tests are written against.
        await mgr.TransitionAsync(id, status);

        // The worktree the diff is taken in belongs to this run, and the run is
        // where the base was pinned — editing the work item since must not move
        // the fork point under a worktree that was already built.
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            StartedAt = DateTime.UtcNow,
            RepositoryId = repo.Id,
            WorktreePath = withWorktree ? WorktreePath : null,
            BranchName = "ild/wi-1-run-x",
            BaseBranchOverride = runBaseBranchOverride,
        });
        db.Context.SaveChanges();

        var branchNames = new Mock<IBranchNameOverrideService>();
        branchNames.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BranchNameVerdict.Usable);

        var controller = new WorkItemsController(
            mgr,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorktreePreviewService>().Object,
            repoManager.Object,
            db.LoopRuns,
            db.Providers,
            NoPackageFeeds.Resolver,
            branchNames.Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
            NullLogger<WorkItemsController>.Instance);

        return (controller, repoManager, db, id);
    }
}
