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
            await controller.CheckBranchName("feature/foo", repoId.ToString(), null, cancellationToken: CancellationToken.None));
        var payload = result.Value!;
        Assert.Null(Read(payload, "error"));
        Assert.Contains("already exists on origin", Read(payload, "warning"));
    }

    /// <summary>
    /// The dialog offers "Attach to existing branch" off <c>existsOnOrigin</c>,
    /// and the check has to be asked the attach question for the warning to
    /// drop once the box is ticked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Branch_name_check_passes_the_attach_flag_and_reports_whether_origin_has_the_name(bool onOrigin)
    {
        var (controller, _, db, repoId, branchNames) = SetupWithService(
            new BranchNameVerdict(null, null, ExistsOnOrigin: onOrigin));
        using var _db = db;

        var result = Assert.IsType<OkObjectResult>(
            await controller.CheckBranchName("feature/foo", repoId.ToString(), "WI-1",
                attachToExistingBranch: true, cancellationToken: CancellationToken.None));

        Assert.Equal(onOrigin, ReadExistsOnOrigin(result.Value!));
        branchNames.Verify(b => b.InspectAsync("feature/foo", repoId, "WI-1", true, It.IsAny<CancellationToken>()), Times.Once);

        static bool? ReadExistsOnOrigin(object payload)
            => (bool?)payload.GetType().GetProperty("existsOnOrigin")!.GetValue(payload);
    }

    [Fact]
    public async Task Create_rejects_an_illegal_base_branch_and_says_which_field()
    {
        var (controller, _, db, repoId) = Setup();
        using var _db = db;

        var result = await controller.Create(new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BaseBranchOverride = "release 1.0",
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        // Reporting this as "Branch name cannot contain spaces." would point the
        // human at the wrong one of the two fields.
        Assert.Contains("Base branch", Read(bad.Value!, "error"));
    }

    [Fact]
    public async Task Create_accepts_and_persists_a_legal_base_branch()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;

        var result = await controller.Create(new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BaseBranchOverride = "  release/1.0  ",
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var id = Assert.IsType<WorkItemView>(created.Value).Id;
        Assert.Equal("release/1.0", (await mgr.GetWorkItemAsync(id))!.BaseBranchOverride);
    }

    [Fact]
    public async Task Update_rejects_an_illegal_base_branch()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;
        var id = await mgr.CreateWorkItemAsync("t", "", repoId);

        var result = await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BaseBranchOverride = "../escape",
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await mgr.GetWorkItemAsync(id))!.BaseBranchOverride);
    }

    [Fact]
    public async Task Update_clears_the_base_branch_when_sent_blank()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;
        var id = await mgr.CreateWorkItemAsync("t", "", repoId, null, false, baseBranchOverride: "release/1.0");

        var result = await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BaseBranchOverride = "",
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Null((await mgr.GetWorkItemAsync(id))!.BaseBranchOverride);
    }

    [Fact]
    public async Task Attach_to_existing_branch_is_saved_on_create_kept_by_an_edit_that_omits_it_and_cleared_when_sent_false()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;

        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new WorkItemCreateRequest
        {
            Title = "t",
            RepositoryId = repoId.ToString(),
            BranchNameOverride = "feature/foo",
            AttachToExistingBranch = true,
        }));
        var id = Assert.IsType<WorkItemView>(created.Value).Id;
        Assert.True((await mgr.GetWorkItemAsync(id))!.AttachToExistingBranch);

        Assert.IsType<OkObjectResult>(await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "renamed",
            RepositoryId = repoId.ToString(),
        }));
        Assert.True((await mgr.GetWorkItemAsync(id))!.AttachToExistingBranch);

        Assert.IsType<OkObjectResult>(await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "renamed",
            RepositoryId = repoId.ToString(),
            AttachToExistingBranch = false,
        }));
        Assert.False((await mgr.GetWorkItemAsync(id))!.AttachToExistingBranch);
    }

    [Fact]
    public async Task Attaching_needs_a_branch_name_on_create_and_on_edit()
    {
        var (controller, mgr, db, repoId) = Setup();
        using var _db = db;

        Assert.IsType<BadRequestObjectResult>(await controller.Create(new WorkItemCreateRequest
        {
            Title = "t", RepositoryId = repoId.ToString(), AttachToExistingBranch = true,
        }));

        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new WorkItemCreateRequest
        {
            Title = "t", RepositoryId = repoId.ToString(),
        }));
        var id = Assert.IsType<WorkItemView>(created.Value).Id;

        // No name stored and none sent.
        Assert.IsType<BadRequestObjectResult>(await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t", RepositoryId = repoId.ToString(), AttachToExistingBranch = true,
        }));
        // A name sent blank is a clear, not a name.
        Assert.IsType<BadRequestObjectResult>(await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t", RepositoryId = repoId.ToString(), BranchNameOverride = "", AttachToExistingBranch = true,
        }));
        Assert.IsType<OkObjectResult>(await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t", RepositoryId = repoId.ToString(), BranchNameOverride = "feature/foo", AttachToExistingBranch = true,
        }));
        Assert.True((await mgr.GetWorkItemAsync(id))!.AttachToExistingBranch);

        // Clearing the name takes the attach with it.
        Assert.IsType<OkObjectResult>(await controller.Update(id, new WorkItemCreateRequest
        {
            Title = "t", RepositoryId = repoId.ToString(), BranchNameOverride = "",
        }));
        Assert.False((await mgr.GetWorkItemAsync(id))!.AttachToExistingBranch);
    }

    private static string? Read(object payload, string property)
        => payload.GetType().GetProperty(property)!.GetValue(payload) as string;

    private static (WorkItemsController controller, WorkItemManager mgr, TestDb db, Guid repoId) Setup(
        BranchNameVerdict? verdict = null)
    {
        var (controller, mgr, db, repoId, _) = SetupWithService(verdict);
        return (controller, mgr, db, repoId);
    }

    private static (WorkItemsController controller, WorkItemManager mgr, TestDb db, Guid repoId, Mock<IBranchNameOverrideService> branchNames) SetupWithService(
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
            db.ServerOptions,
            engine: new Mock<ILoopEngine>().Object);

        var branchNames = new Mock<IBranchNameOverrideService>();
        branchNames.Setup(b => b.InspectAsync(It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(verdict ?? BranchNameVerdict.Usable);

        var controller = new WorkItemsController(
            mgr,
            new Mock<ILoopEngine>().Object,
            new Mock<IWorktreePreviewService>().Object,
            new Mock<IRepositoryManager>().Object,
            db.LoopRuns,
            db.Providers,
            NoPackageFeeds.Resolver,
            branchNames.Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment(),
            NullLogger<WorkItemsController>.Instance);

        return (controller, mgr, db, repo.Id, branchNames);
    }
}
