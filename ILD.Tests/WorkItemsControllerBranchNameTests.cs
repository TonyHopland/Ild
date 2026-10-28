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
/// The custom branch name reaches git verbatim — as a branch and, through the
/// worktree root, as a directory — so an illegal one is refused at the API on
/// both the way in and every edit after. Editing is how a human clears a branch
/// conflict, which makes the edit path a first-class way to introduce a bad
/// name, not an afterthought.
///
/// A name that is merely <em>taken</em> is a different matter and deliberately
/// not tested here: it never blocks a save. See
/// <see cref="BranchNameOverrideServiceTests"/>.
///
/// The base branch — the ref a run is built from — goes through the same rules
/// on the same two paths, so it is covered here too. Whether it actually exists
/// on origin is not an API question: that is settled at run start, where it
/// fails the Start node (see <see cref="StartNodeExecutorTests"/>).
/// </summary>
public class WorkItemsControllerBranchNameTests
{
    public static TheoryData<string, string, string, string, string?> Fields => new()
    {
        { "branch", "feature foo", "  feature/foo  ", "feature/foo", null },
        { "base", "release 1.0", "  release/1.0  ", "release/1.0", "Base branch" },
    };

    public static TheoryData<string> FieldNames => new() { "branch", "base" };

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Create_rejects_an_illegal_name(string field, string illegal, string legalInput, string legalStored, string? errorNames)
    {
        var (controller, _, db, repoId) = Setup();
        using var _db = db;

        var result = await controller.Create(Request(field, repoId, illegal));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        // Reporting a base branch as "Branch name cannot contain spaces." would point the
        // human at the wrong one of the two fields.
        if (errorNames is not null) Assert.Contains(errorNames, Read(bad.Value!, "error"));
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Create_accepts_and_persists_a_legal_name(string field, string illegal, string legalInput, string legalStored, string? errorNames)
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;

        var result = await controller.Create(Request(field, repoId, legalInput));

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var id = Assert.IsType<WorkItemView>(created.Value).Id;
        Assert.Equal(legalStored, Stored(field, (await mgr.GetWorkItemAsync(id))!));
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public async Task Update_rejects_an_illegal_name(string field)
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;
        var id = await mgr.CreateWorkItemAsync(TestActors.Human, "t", "", repoId);

        var result = await controller.Update(id, Request(field, repoId, "../escape"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(Stored(field, (await mgr.GetWorkItemAsync(id))!));
    }

    [Theory]
    [MemberData(nameof(FieldNames))]
    public async Task Update_clears_the_name_when_sent_blank(string field)
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;
        var id = field == "branch"
            ? await mgr.CreateWorkItemAsync(TestActors.Human, "t", "", repoId, null, false, branchNameOverride: "feature/foo")
            : await mgr.CreateWorkItemAsync(TestActors.Human, "t", "", repoId, null, false, baseBranchOverride: "release/1.0");

        var result = await controller.Update(id, Request(field, repoId, ""));

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(Stored(field, (await mgr.GetWorkItemAsync(id))!));
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

    private static WorkItemCreateRequest Request(string field, Guid repoId, string value) => new()
    {
        Title = "t",
        RepositoryId = repoId.ToString(),
        BranchNameOverride = field == "branch" ? value : null,
        BaseBranchOverride = field == "base" ? value : null,
    };

    private static string? Stored(string field, WorkItemView view)
        => field == "branch" ? view.BranchNameOverride : view.BaseBranchOverride;

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
        eventLog.Setup(e => e.AppendAsync(It.IsAny<Guid>(), It.IsAny<EventType>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<string?>()))
            .ReturnsAsync(1L);

        var mgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object,
            db.Providers,
            eventLog.Object,
            db.LoopRuns,
            db.ServerClient,
            db.ServerOptions, TestActors.Instance,
            engine: new Mock<ILoopEngine>().Object);

        var branchNames = new Mock<IBranchNameOverrideService>();
        branchNames.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(verdict ?? BranchNameVerdict.Usable);

        var controller = TestActors.SignedIn(new WorkItemsController(
            mgr,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorktreePreviewService>().Object,
            new Mock<IRepositoryManager>().Object,
            db.LoopRuns,
            db.Providers,
            NoPackageFeeds.Resolver,
            branchNames.Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
            NullLogger<WorkItemsController>.Instance));

        return (controller, mgr, db, repo.Id);
    }
}
