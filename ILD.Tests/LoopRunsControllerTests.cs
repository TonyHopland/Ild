using ILD.Api.Controllers;
using ILD.Api.Services;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using System.Text;

namespace ILD.Tests;

public class LoopRunsControllerTests
{
    [Fact]
    public async Task Delete_reclaims_worktree_and_branch_before_removing_the_run()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            Status = LoopRunStatus.Completed,
            WorktreePath = "/tmp/wt/run-a",
            BranchName = "ild/wi-1-run-a",
        };
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetByIdAsync(run.Id)).ReturnsAsync(run);
        store.Setup(s => s.DeleteAsync(run.Id)).ReturnsAsync(true);
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(run)).ReturnsAsync(true);

        var controller = BuildController(store, reclaimer);
        var result = await controller.Delete(run.Id.ToString());

        Assert.IsType<NoContentResult>(result);
        reclaimer.Verify(r => r.ReclaimLocalStateAsync(run), Times.Once);
        store.Verify(s => s.DeleteAsync(run.Id), Times.Once);
    }

    [Fact]
    public async Task Delete_refuses_when_reclaim_fails_so_git_state_is_never_orphaned()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            Status = LoopRunStatus.Completed,
            WorktreePath = "/tmp/wt/run-a",
        };
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetByIdAsync(run.Id)).ReturnsAsync(run);
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(run)).ReturnsAsync(false);

        var controller = BuildController(store, reclaimer);
        var result = await controller.Delete(run.Id.ToString());

        // The row must survive a failed reclaim — deleting it would leave the
        // worktree/branch orphaned with nothing pointing at them.
        Assert.IsType<ConflictObjectResult>(result);
        store.Verify(s => s.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(LoopRunStatus.Running)]
    [InlineData(LoopRunStatus.WaitingHuman)]
    public async Task Cleanup_refuses_a_run_that_can_still_be_resumed(LoopRunStatus status)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            Status = status,
            WorktreePath = "/tmp/wt/run-a",
            BranchName = "ild/wi-1-run-a",
        };
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetByIdAsync(run.Id)).ReturnsAsync(run);
        var reclaimer = new Mock<IRunReclaimer>();

        var result = await BuildController(store, reclaimer).Cleanup(run.Id.ToString());

        Assert.IsType<BadRequestObjectResult>(result);
        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.Equal("ild/wi-1-run-a", run.BranchName);
    }

    private static LoopRunsController BuildController(
        Mock<ILoopRunStore> store, Mock<IRunReclaimer> reclaimer, IAdapterSessionSnapshotStore? snapshots = null)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(w => w.DeleteRunAsync(It.IsAny<Guid>())).Returns<Guid>(store.Object.DeleteAsync);
        return TestActors.SignedIn(new LoopRunsController(new Mock<ILoopEngine>().Object, new Mock<IEventLogService>().Object, store.Object,
            snapshots ?? new Mock<IAdapterSessionSnapshotStore>().Object,
            new InteractiveShellSessionService(NullLogger<InteractiveShellSessionService>.Instance),
            reclaimer.Object, workItems.Object, NoPackageFeeds.Resolver, new Mock<IRunConversationService>().Object, Mock.Of<ILoopTemplateStore>(), Mock.Of<IGlobalVariableStore>(),
            Mock.Of<ISchedulerSettingsService>(), claudeProjects: TestClaudeProjectFolders.Instance));
    }

    [Fact]
    public async Task GetById_includes_available_sessions_for_run()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TestDb();
        var run = await SeedRunAsync(db);
        var runId = run.Id;
        var snapshots = new AdapterSessionSnapshotStore(db.Context);
        const string current = "{\"id\":\"ses_current\"}\n{\"n\":1}\n";
        await SessionSnapshotTestSupport.SeedAsync(snapshots, SessionSnapshotKey.ForRun(runId, "OpenCode", "ses_current"), "opencode-json", current, ct);
        await SessionSnapshotTestSupport.SeedAsync(snapshots, SessionSnapshotKey.ForRun(runId, "OpenCode", "ses_old"), "opencode-json", "{\"id\":\"ses_old\"}\n", ct);
        await SetUpdatedAtAsync(db, runId, "ses_current", DateTime.UtcNow.AddMinutes(-5));
        await SetUpdatedAtAsync(db, runId, "ses_old", DateTime.UtcNow.AddMinutes(-20));
        var legacyId = await SessionSnapshotTestSupport.SeedLegacyAsync(
            db.Context, runId, null, "ClaudeCode", "ses_legacy",
            SessionSnapshotTestSupport.LegacyClaudeWrapper("ses_legacy", "{\"type\":\"user\"}"), ct);

        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(run);
        store.Setup(s => s.GetRunNodesWithNodeAsync(runId)).ReturnsAsync(Array.Empty<LoopRunNode>());
        store.Setup(s => s.GetSessionBindingsAsync(runId)).ReturnsAsync(new[]
        {
            new LoopRunSessionBinding
            {
                LoopRunId = runId,
                AdapterName = "OpenCode",
                PlaceholderId = "research",
                SessionId = "ses_current",
            },
        });
        store.Setup(s => s.GetVariablesAsync(runId)).ReturnsAsync(Array.Empty<LoopRunVariable>());

        var result = await BuildController(store, new Mock<IRunReclaimer>(), snapshots).GetById(runId.ToString());

        var payload = Assert.IsType<OkObjectResult>(result).Value!;
        var sessions = (System.Collections.IEnumerable)payload.GetType().GetProperty("availableSessions")!.GetValue(payload)!;
        var items = sessions.Cast<object>().ToList();
        object? Field(object item, string name) => item.GetType().GetProperty(name)!.GetValue(item);
        // Newest first by UpdatedAt, falling back to CreatedAt for a row never updated.
        Assert.Equal(new[] { "ses_legacy", "ses_current", "ses_old" }, items.Select(i => (string)Field(i, "sessionId")!));

        var currentItem = items[1];
        Assert.Equal("OpenCode", Field(currentItem, "adapterName"));
        Assert.Equal(true, Field(currentItem, "isCurrent"));
        Assert.Equal("research", Assert.Single(((System.Collections.IEnumerable)Field(currentItem, "placeholders")!).Cast<object>()));
        Assert.NotNull(Field(currentItem, "createdAt"));
        Assert.NotNull(Field(currentItem, "updatedAt"));
        Assert.Equal("opencode-json", Field(currentItem, "format"));
        Assert.Equal((long)Encoding.UTF8.GetByteCount(current), Convert.ToInt64(Field(currentItem, "byteLength")));
        Assert.Equal(2, Convert.ToInt32(Field(currentItem, "eventCount")));
        Assert.Equal(false, Field(items[2], "isCurrent"));

        var legacy = items[0];
        Assert.Equal("ClaudeCode", Field(legacy, "adapterName"));
        Assert.Null(Field(legacy, "format"));
        Assert.Null(Field(legacy, "byteLength"));
        Assert.Null(Field(legacy, "eventCount"));
        Assert.NotNull(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(db.Fresh(), legacyId, ct));
    }

    private static async Task<LoopRun> SeedRunAsync(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"template-{Guid.NewGuid():N}" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return run;
    }

    private static Task SetUpdatedAtAsync(TestDb db, Guid runId, string sessionId, DateTime updatedAt)
        => db.Context.AdapterSessionSnapshots
            .Where(s => s.LoopRunId == runId && s.SessionId == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, updatedAt), TestContext.Current.CancellationToken);

    [Theory]
    // The UI has to say WHY a run is parked — a provider interruption offers a
    // different Resume than a human's Halt — and the only place that is recorded
    // is the run's own halt reason. Inferring it from the node rows cannot work:
    // a human's Halt also ends its node Interrupted with an error.
    [InlineData(HaltReason.Throttled, "Throttled")]
    [InlineData(HaltReason.Shutdown, "Shutdown")]
    [InlineData(null, null)]
    public async Task GetById_projects_the_halt_reason(HaltReason? haltReason, string? expected)
    {
        var runId = Guid.NewGuid();
        var run = new LoopRun
        {
            Id = runId,
            WorkItemId = "wi-1",
            Status = LoopRunStatus.WaitingHuman,
            IsHalted = true,
            HaltReason = haltReason,
        };

        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetByIdAsync(runId)).ReturnsAsync(run);
        store.Setup(s => s.GetRunNodesWithNodeAsync(runId)).ReturnsAsync(Array.Empty<LoopRunNode>());
        store.Setup(s => s.GetSessionBindingsAsync(runId)).ReturnsAsync(Array.Empty<LoopRunSessionBinding>());
        store.Setup(s => s.GetVariablesAsync(runId)).ReturnsAsync(Array.Empty<LoopRunVariable>());

        using var db = new TestDb();

        var result = await BuildController(store, new Mock<IRunReclaimer>(), new AdapterSessionSnapshotStore(db.Context)).GetById(runId.ToString());

        var payload = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.Equal(expected, payload.GetType().GetProperty("haltReason")!.GetValue(payload));
    }

    [Fact]
    public async Task GetAll_projects_the_halt_reason()
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            Status = LoopRunStatus.WaitingHuman,
            IsHalted = true,
            HaltReason = HaltReason.Throttled,
            RunNodes = new List<LoopRunNode>(),
        };
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetAllAsync(0, 100)).ReturnsAsync(new[] { run });

        var result = await BuildController(store, new Mock<IRunReclaimer>()).GetAll();

        var payload = (System.Collections.IEnumerable)Assert.IsType<OkObjectResult>(result).Value!;
        var item = Assert.Single(payload.Cast<object>());
        Assert.Equal("Throttled", item.GetType().GetProperty("haltReason")!.GetValue(item));
    }
}
