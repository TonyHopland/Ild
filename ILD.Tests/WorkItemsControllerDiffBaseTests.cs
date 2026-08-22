using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
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

        repoManager.Verify(m => m.ListWorktreeFilesAsync(WorktreePath, "release/1.0"), Times.Once);
        repoManager.Verify(m => m.ListWorktreeFilesAsync(It.IsAny<string>(), "main"), Times.Never);
    }

    [Fact]
    public async Task File_content_diffs_against_the_same_base_as_the_file_list()
    {
        // Two endpoints, one fork point — a file whose diff is computed against a
        // different ref than the list it came from is worse than no diff at all.
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: "release/1.0");
        using var _db = db;

        await controller.GetFileContent(WorkItemId, "src/app.ts");

        repoManager.Verify(m => m.ReadWorktreeFileAsync(WorktreePath, "src/app.ts", "release/1.0"), Times.Once);
    }

    [Fact]
    public async Task Without_an_override_the_diff_still_anchors_on_the_repository_default()
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;

        await controller.GetFiles(WorkItemId);

        repoManager.Verify(m => m.ListWorktreeFilesAsync(WorktreePath, "main"), Times.Once);
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

        repoManager.Verify(m => m.WriteWorktreeFileAsync(WorktreePath, "src/app.ts", "edited", "release/1.0"), Times.Once);
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
            m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task A_file_the_worktree_refuses_to_take_is_a_bad_request()
    {
        var (controller, repoManager, db, _) = await SetupAsync(runBaseBranchOverride: null);
        using var _db = db;
        repoManager
            .Setup(m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((WorktreeFileContentResponse?)null);

        var result = await controller.SaveFileContent(
            WorkItemId,
            new WorktreeFileSaveRequest { Path = "../escape.ts", Content = "edited" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private const string WorkItemId = "1";
    private const string WorktreePath = "/tmp/ild-difftest-worktree";

    private static async Task<(WorkItemsController Controller, Mock<IRepositoryManager> RepoManager, TestDb Db, string Id)> SetupAsync(
        string? runBaseBranchOverride)
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
        repoManager.Setup(m => m.ListWorktreeFilesAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<WorktreeFileEntry>());
        repoManager.Setup(m => m.ReadWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((WorktreeFileContentResponse?)null);
        repoManager.Setup(m => m.WriteWorktreeFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string _, string path, string content, string? __) =>
                new WorktreeFileContentResponse { Path = path, ChangeStatus = "modified", Content = content });

        var mgr = new WorkItemManager(
            repoManager.Object,
            db.Providers,
            eventLog.Object,
            db.LoopRuns,
            db.ServerClient,
            db.ServerOptions,
            engine: new Mock<ILoopEngine>().Object);

        var id = await mgr.CreateWorkItemAsync("t", "", repo.Id);

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
            WorktreePath = WorktreePath,
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
            branchNames.Object,
            NullLogger<WorkItemsController>.Instance);

        return (controller, repoManager, db, id);
    }
}
