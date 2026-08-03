using ILD.Api.Controllers;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The custom branch name reaches git verbatim — as a branch and, through the
/// worktree root, as a directory — so an illegal one is refused at the API on
/// both the way in and every edit after. Editing is how a human clears a branch
/// conflict, which makes the edit path a first-class way to introduce a bad
/// name, not an afterthought.
///
/// A name that is merely <em>taken</em> is a different matter and deliberately
/// not tested here: it never blocks a save. See
/// <see cref="BranchNameOverrideServiceTests"/>.
/// </summary>
public class WorkItemsControllerBranchNameTests
{
    [Fact]
    public async Task Create_rejects_an_illegal_branch_name()
    {
        var (controller, _, db, repoId) = Setup();
        using var _db = db;

        var result = await controller.Create(new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BranchNameOverride = "feature foo",
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_accepts_and_persists_a_legal_branch_name()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;

        var result = await controller.Create(new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BranchNameOverride = "  feature/foo  ",
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var id = Assert.IsType<WorkItemView>(created.Value).Id;
        Assert.Equal("feature/foo", (await mgr.GetWorkItemAsync(id))!.BranchNameOverride);
    }

    [Fact]
    public async Task Update_rejects_an_illegal_branch_name()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;
        var id = await mgr.CreateWorkItemAsync("t", "", repoId);

        var result = await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BranchNameOverride = "../escape",
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await mgr.GetWorkItemAsync(id))!.BranchNameOverride);
    }

    [Fact]
    public async Task Update_clears_the_branch_name_when_sent_blank()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;
        var id = await mgr.CreateWorkItemAsync("t", "", repoId, null, false, branchNameOverride: "feature/foo");

        var result = await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BranchNameOverride = "",
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Null((await mgr.GetWorkItemAsync(id))!.BranchNameOverride);
    }

    [Fact]
    public async Task Branch_name_check_reports_a_conflict_as_a_warning_not_an_error()
    {
        var (controller, _, db, repoId) = Setup(
            new BranchNameVerdict(null, "Branch `feature/foo` already exists on origin."));
        using var _db = db;

        var result = Assert.IsType<OkObjectResult>(
            await controller.CheckBranchName("feature/foo", repoId.ToString(), null, CancellationToken.None));
        var payload = result.Value!;
        Assert.Null(Read(payload, "error"));
        Assert.Contains("already exists on origin", Read(payload, "warning"));
    }

    private static string? Read(object payload, string property)
        => payload.GetType().GetProperty(property)!.GetValue(payload) as string;

    private static (WorkItemsController controller, WorkItemManager mgr, TestDb db, Guid repoId) Setup(
        BranchNameVerdict? verdict = null)
    {
        var db = new TestDb();
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example" };
        var repo = new Repository { Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = remote.Id, CloneUrl = "https://example/repo.git" };
        db.Context.RemoteProviders.Add(remote);
        db.Context.Repositories.Add(repo);
        db.Context.SaveChanges();

        var eventLog = new Mock<IEventLogService>();
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(1L);

        var mgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object,
            db.Providers,
            eventLog.Object,
            db.LoopRuns,
            db.ServerClient,
            db.ServerOptions,
            engine: new Mock<ILoopEngine>().Object);

        var branchNames = new Mock<IBranchNameOverrideService>();
        branchNames.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(verdict ?? BranchNameVerdict.Usable);

        var controller = new WorkItemsController(
            mgr,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorktreePreviewService>().Object,
            new Mock<IRepositoryManager>().Object,
            db.LoopRuns,
            db.Providers,
            branchNames.Object,
            NullLogger<WorkItemsController>.Instance);

        return (controller, mgr, db, repo.Id);
    }
}
