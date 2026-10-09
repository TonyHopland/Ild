using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The branch web link rides only on the single work item read. The pushed
/// check runs git, so the board's list reads never pay for it, and a failing
/// check never costs the read itself.
/// </summary>
public class WorkItemsControllerBranchUrlTests
{
    private const string Branch = "ild/wi-x-run-1";

    [Fact]
    public async Task GetById_carries_the_link_of_a_pushed_branch()
    {
        var (controller, db, repoMgr, id, worktree) = await SetupAsync();
        using var _db = db;
        try
        {
            repoMgr.Setup(r => r.RemoteBranchExistsAsync(worktree, Branch)).ReturnsAsync(true);

            var ok = Assert.IsType<OkObjectResult>(await controller.GetById(id));

            Assert.Equal("https://example/repo/src/branch/" + Branch, Assert.IsType<WorkItemView>(ok.Value).BranchUrl);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task GetById_still_answers_without_a_link_when_the_check_fails()
    {
        var (controller, db, repoMgr, id, worktree) = await SetupAsync();
        using var _db = db;
        try
        {
            repoMgr.Setup(r => r.RemoteBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("git exploded"));

            var ok = Assert.IsType<OkObjectResult>(await controller.GetById(id));

            var view = Assert.IsType<WorkItemView>(ok.Value);
            Assert.Equal(id, view.Id);
            Assert.Equal(Branch, view.BranchName);
            Assert.Null(view.BranchUrl);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [Fact]
    public async Task List_reads_never_run_the_pushed_check_or_carry_a_link()
    {
        var (controller, db, repoMgr, id, worktree) = await SetupAsync();
        using var _db = db;
        try
        {
            repoMgr.Setup(r => r.RemoteBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

            var all = Assert.IsType<OkObjectResult>(await controller.GetAll());
            var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<WorkItemView>>(all.Value));
            Assert.Equal(id, listed.Id);
            Assert.Null(listed.BranchUrl);

            var page = Assert.IsType<OkObjectResult>(await controller.GetPage());
            var items = (IEnumerable<WorkItemView>)page.Value!.GetType().GetProperty("items")!.GetValue(page.Value)!;
            Assert.Equal(id, Assert.Single(items).Id);
            Assert.Null(Assert.Single(items).BranchUrl);

            repoMgr.Verify(r => r.RemoteBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    private static async Task<(WorkItemsController controller, TestDb db, Mock<IRepositoryManager> repoMgr, string id, string worktree)> SetupAsync()
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<EventType>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .ReturnsAsync(1L);

        var repoMgr = new Mock<IRepositoryManager>();
        var mgr = new WorkItemManager(
            repoMgr.Object,
            db.Providers,
            eventLog.Object,
            db.LoopRuns,
            db.ServerClient,
            db.ServerOptions,
            engine: new Mock<ILoopEngine>().Object);

        var id = await mgr.CreateWorkItemAsync("Link me", "", repo.Id);
        var worktree = Path.Combine(Path.GetTempPath(), "ild-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = version.Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.Running,
            WorktreePath = worktree,
            BranchName = Branch,
            RepositoryId = repo.Id,
        });
        db.Context.SaveChanges();

        var controller = new WorkItemsController(
            mgr,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorktreePreviewService>().Object,
            repoMgr.Object,
            db.LoopRuns,
            db.Providers,
            NoPackageFeeds.Resolver,
            new Mock<IBranchNameOverrideService>().Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
            NullLogger<WorkItemsController>.Instance);

        return (controller, db, repoMgr, id, worktree);
    }
}
