using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class WorktreeRetentionSweeperTests
{
    [Fact]
    public async Task Sweep_reclaims_old_terminal_run_for_done_workitem()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/run-a", branch: "ild/wi-x-run-a");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 30).SweepOnceAsync(CancellationToken.None);

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == run.Id)), Times.AtLeastOnce);
        Assert.Null(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task Sweep_never_reclaims_pinned_or_non_terminal_run(int retentionDays)
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/pinned", branch: "ild/wi-x-run-pinned", retain: true);
        // Retried back to Running: still carries the CompletedAt of its earlier finish.
        var running = SeedRun(db, version.Id, workItemId, LoopRunStatus.Running,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/running", branch: "ild/wi-x-run-running");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays).SweepOnceAsync(CancellationToken.None);

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(running.Id));
    }

    [Theory]
    // 0 = the next sweep after the item is Done, whatever terminal state the run ended in.
    [InlineData(0, LoopRunStatus.Completed, 1, true)]
    [InlineData(0, LoopRunStatus.Failed, 1, true)]
    [InlineData(0, LoopRunStatus.Cancelled, 1, true)]
    // A positive window still keeps a Done item's run until it has been terminal that long.
    [InlineData(30, LoopRunStatus.Completed, 10 * 24 * 60, false)]
    public async Task Sweep_reclaims_done_items_run_once_terminal_longer_than_retention(
        int retentionDays, LoopRunStatus status, int minutesSinceCompleted, bool reclaimed)
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, status,
            completedAt: DateTime.UtcNow.AddMinutes(-minutesSinceCompleted),
            worktree: "/tmp/wt/done", branch: "ild/wi-x-run-done");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays).SweepOnceAsync(CancellationToken.None);

        if (reclaimed)
        {
            reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == run.Id)), Times.AtLeastOnce);
            Assert.Null(await db.LoopRuns.GetByIdAsync(run.Id));
        }
        else
        {
            var kept = await ReadAsync(db, run.Id);
            Assert.NotNull(kept);
            Assert.Null(kept!.WorktreePath);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task Sweep_keeps_current_run_of_active_workitem(int retentionDays)
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        // A Failed run parked in HumanFeedback is the work item's current run.
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Failed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/active", branch: "ild/wi-x-run-active");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.HumanFeedback);
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays).SweepOnceAsync(CancellationToken.None);

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task Sweep_keeps_superseded_run_until_workitem_is_done(int retentionDays)
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        // Old failed run, superseded by a newer running run for the same work item.
        var old = SeedRun(db, version.Id, workItemId, LoopRunStatus.Failed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/old", branch: "ild/wi-x-run-old",
            startedAt: DateTime.UtcNow.AddDays(-41));
        SeedRun(db, version.Id, workItemId, LoopRunStatus.Running,
            completedAt: null, worktree: "/tmp/wt/new", branch: "ild/wi-x-run-new",
            startedAt: DateTime.UtcNow.AddDays(-1));

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Running);
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays).SweepOnceAsync(CancellationToken.None);

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(old.Id));

        // The item is Done: the next sweep reclaims the old run.
        workItems.Setup(x => x.GetWorkItemAsync(workItemId))
            .ReturnsAsync(new WorkItemView { Id = workItemId, Status = RemoteWorkItemStatus.Done });
        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays).SweepOnceAsync(CancellationToken.None);

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == old.Id)), Times.AtLeastOnce);
        Assert.Null(await db.LoopRuns.GetByIdAsync(old.Id));
    }

    [Fact]
    public async Task One_sweep_reaches_every_done_items_run_past_any_number_of_older_kept_runs()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var doneItem = Guid.NewGuid().ToString();
        var activeItem = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        // Oldest first: Done-item runs (deleted as the pass goes), then more kept
        // runs of an active item than one candidate batch holds, then the newest
        // Done-item run behind them all.
        var doneRuns = new List<Guid>();
        var keptRuns = new List<Guid>();
        for (var i = 0; i < 250; i++)
            doneRuns.Add(AddRun(db, version.Id, doneItem, now.AddDays(-60).AddMinutes(i)));
        for (var i = 0; i < 250; i++)
            keptRuns.Add(AddRun(db, version.Id, activeItem, now.AddDays(-50).AddMinutes(i)));
        doneRuns.Add(AddRun(db, version.Id, doneItem, now.AddDays(-1)));
        db.Context.SaveChanges();
        db.Context.ChangeTracker.Clear();

        var workItems = WorkItemsReturning(
            (doneItem, RemoteWorkItemStatus.Done),
            (activeItem, RemoteWorkItemStatus.Running));
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 0).SweepOnceAsync(CancellationToken.None);

        foreach (var id in doneRuns)
            Assert.Null(await db.LoopRuns.GetByIdAsync(id));
        foreach (var id in keptRuns)
            Assert.NotNull(await db.LoopRuns.GetByIdAsync(id));
    }

    [Fact]
    public async Task Sweep_keeps_run_row_when_reclaim_fails_so_next_sweep_retries()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/stuck", branch: "ild/wi-x-run-stuck");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(false);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 30).SweepOnceAsync(CancellationToken.None);

        // Reclaim was attempted but reported failure: the row must survive so a
        // later sweep can retry — deleting it would orphan the worktree forever.
        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == run.Id)), Times.AtLeastOnce);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));

        // A later sweep where the reclaim succeeds deletes the row.
        var retryReclaimer = ReclaimerReturning(true);
        await BuildSweeper(db, workItems.Object, retryReclaimer.Object, retentionDays: 30).SweepOnceAsync(CancellationToken.None);
        Assert.Null(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    [Fact]
    public async Task Sweep_keeps_run_row_when_its_pending_proposals_cannot_be_withdrawn_so_next_sweep_retries()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/proposer", branch: "ild/wi-x-run-proposer");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        workItems.Setup(x => x.WithdrawPendingProposalsOfRunAsync(It.IsAny<WorkItemActor>(), run.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused (workitem-server:8081)"));

        await BuildSweeper(db, workItems.Object, ReclaimerReturning(true).Object, retentionDays: 30).SweepOnceAsync(CancellationToken.None);

        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));

        workItems.Setup(x => x.WithdrawPendingProposalsOfRunAsync(It.IsAny<WorkItemActor>(), run.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await BuildSweeper(db, workItems.Object, ReclaimerReturning(true).Object, retentionDays: 30).SweepOnceAsync(CancellationToken.None);

        Assert.Null(await db.LoopRuns.GetByIdAsync(run.Id));
        workItems.Verify(x => x.WithdrawPendingProposalsOfRunAsync(It.IsAny<WorkItemActor>(), run.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Sweep_rereads_each_run_from_the_database_so_a_pin_made_mid_pass_holds()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var first = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-2), worktree: "/tmp/wt/first", branch: "ild/wi-x-run-first");
        var second = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-1), worktree: "/tmp/wt/second", branch: "ild/wi-x-run-second");

        // The user pins the second run while the sweep reclaims the first.
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).Returns(async (LoopRun run) =>
        {
            if (run.Id == first.Id)
            {
                await using var user = db.Fresh();
                (await user.LoopRuns.SingleAsync(r => r.Id == second.Id)).Retain = true;
                await user.SaveChangesAsync();
            }
            return true;
        });

        // Wired per scope as in production: looking up the work item loads, and
        // tracks, all of its runs in the scope's context, as WorkItemManager does.
        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetRunRetentionDaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var services = new ServiceCollection();
        services.AddScoped(_ => db.Fresh());
        services.AddScoped<ILoopRunStore>(sp => new LoopRunStore(sp.GetRequiredService<AppDbContext>()));
        services.AddScoped(sp =>
        {
            var store = sp.GetRequiredService<ILoopRunStore>();
            var m = new Mock<IWorkItemManager>();
            m.Setup(x => x.DeleteRunAsync(It.IsAny<Guid>())).Returns<Guid>(store.DeleteAsync);
            m.Setup(x => x.GetWorkItemAsync(workItemId)).Returns(async () =>
            {
                await store.GetAllByWorkItemAsync(workItemId);
                return new WorkItemView { Id = workItemId, Status = RemoteWorkItemStatus.Done };
            });
            return m.Object;
        });
        services.AddScoped<IAdapterSessionSnapshotStore>(sp => new AdapterSessionSnapshotStore(sp.GetRequiredService<AppDbContext>()));
        services.AddSingleton(reclaimer.Object);
        services.AddSingleton(settings.Object);
        services.AddSingleton(TestActors.Instance);
        services.AddRealFinishedRunCleaner();

        await NewSweeper(services.BuildServiceProvider()).SweepOnceAsync(CancellationToken.None);

        await using var check = db.Fresh();
        Assert.False(await check.LoopRuns.AnyAsync(r => r.Id == first.Id));
        Assert.True(await check.LoopRuns.AnyAsync(r => r.Id == second.Id && r.Retain));
        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == second.Id)), Times.Never);
    }

    [Fact]
    public async Task Sweep_looks_up_each_work_items_status_once_per_pass()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var active = Guid.NewGuid().ToString();
        var done = Guid.NewGuid().ToString();
        var kept = Enumerable.Range(1, 3)
            .Select(i => SeedRun(db, version.Id, active, LoopRunStatus.Failed,
                completedAt: DateTime.UtcNow.AddDays(-i), worktree: $"/tmp/wt/kept-{i}", branch: $"ild/wi-x-run-kept-{i}"))
            .ToList();
        var reclaimable = Enumerable.Range(1, 2)
            .Select(i => SeedRun(db, version.Id, done, LoopRunStatus.Completed,
                completedAt: DateTime.UtcNow.AddHours(-i), worktree: $"/tmp/wt/done-{i}", branch: $"ild/wi-x-run-done-{i}"))
            .ToList();

        var workItems = WorkItemsReturning((active, RemoteWorkItemStatus.Running), (done, RemoteWorkItemStatus.Done));

        await BuildSweeper(db, workItems.Object, ReclaimerReturning(true).Object, retentionDays: 0).SweepOnceAsync(CancellationToken.None);

        workItems.Verify(x => x.GetWorkItemAsync(active), Times.Once);
        workItems.Verify(x => x.GetWorkItemAsync(done), Times.Once);
        foreach (var run in kept)
            Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));
        foreach (var run in reclaimable)
            Assert.Null(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    public enum Item { Running, HumanFeedback, Done, Gone }

    /// <summary>
    /// The backstop: whatever a finished run left behind — from before end-of-run removal
    /// existed, a crash, or a removal that failed — goes on the next pass, while the run
    /// row stays until retention.
    /// </summary>
    [Theory]
    [InlineData(LoopRunStatus.Completed, Item.Running, true)]
    [InlineData(LoopRunStatus.Completed, Item.Done, true)]
    [InlineData(LoopRunStatus.Cancelled, Item.HumanFeedback, true)]
    [InlineData(LoopRunStatus.Cancelled, Item.Gone, true)]
    [InlineData(LoopRunStatus.Failed, Item.Done, true)]
    [InlineData(LoopRunStatus.Failed, Item.Gone, true)]
    // A Failed run of an item still in play may be reopened; it keeps everything.
    [InlineData(LoopRunStatus.Failed, Item.HumanFeedback, false)]
    [InlineData(LoopRunStatus.Failed, Item.Running, false)]
    [InlineData(LoopRunStatus.WaitingHuman, Item.Done, false)]
    [InlineData(LoopRunStatus.Running, Item.Done, false)]
    public async Task Sweep_removes_what_a_finished_run_left_behind_and_keeps_the_run_until_retention(
        LoopRunStatus status, Item item, bool removed)
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, status,
            completedAt: status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman ? null : DateTime.UtcNow.AddDays(-1),
            worktree: "/tmp/wt/left", branch: "ild/wi-x-run-left");
        await FinishedRunCleanerWiring.SeedSnapshotAsync(db, run.Id);
        var workItems = item == Item.Gone
            ? new Mock<IWorkItemManager>()
            : WorkItemsReturning(workItemId, item switch
            {
                Item.Running => RemoteWorkItemStatus.Running,
                Item.HumanFeedback => RemoteWorkItemStatus.HumanFeedback,
                _ => RemoteWorkItemStatus.Done,
            });
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 14).SweepOnceAsync(CancellationToken.None);

        var after = await ReadAsync(db, run.Id);
        Assert.NotNull(after);
        if (removed)
        {
            reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == run.Id)), Times.Once);
            Assert.Null(after!.WorktreePath);
            Assert.Null(after.BranchName);
            Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(db, run.Id));
        }
        else
        {
            reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
            Assert.Equal("/tmp/wt/left", after!.WorktreePath);
            Assert.Equal("ild/wi-x-run-left", after.BranchName);
            Assert.Equal(1, FinishedRunCleanerWiring.SnapshotCount(db, run.Id));
        }
    }

    [Fact]
    public async Task Sweep_deletes_snapshots_a_finished_run_left_behind_without_a_worktree()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-1), worktree: null, branch: null);
        await FinishedRunCleanerWiring.SeedSnapshotAsync(db, run.Id);

        await BuildSweeper(db, WorkItemsReturning(workItemId, RemoteWorkItemStatus.Running).Object,
            ReclaimerReturning(true).Object, retentionDays: 14)
            .SweepOnceAsync(CancellationToken.None);

        Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(db, run.Id));
        Assert.NotNull(await ReadAsync(db, run.Id));
    }

    [Fact]
    public async Task Sweep_leaves_a_finished_run_the_engine_still_drives_alone()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Cancelled,
            completedAt: DateTime.UtcNow.AddDays(-1), worktree: "/tmp/wt/owned", branch: "ild/wi-x-run-owned");
        await FinishedRunCleanerWiring.SeedSnapshotAsync(db, run.Id);
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done).Object,
            reclaimer.Object, retentionDays: 14, run.Id)
            .SweepOnceAsync(CancellationToken.None);

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.Equal("/tmp/wt/owned", (await ReadAsync(db, run.Id))!.WorktreePath);
        Assert.Equal(1, FinishedRunCleanerWiring.SnapshotCount(db, run.Id));
    }

    [Fact]
    public async Task A_pinned_run_keeps_everything_until_it_is_unpinned_and_then_the_next_sweep_removes_its_leftovers()
    {
        using var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-1), worktree: "/tmp/wt/pinned", branch: "ild/wi-x-run-pinned", retain: true);
        await FinishedRunCleanerWiring.SeedSnapshotAsync(db, run.Id);
        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(true);

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 14).SweepOnceAsync(CancellationToken.None);

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.Equal("/tmp/wt/pinned", (await ReadAsync(db, run.Id))!.WorktreePath);
        Assert.Equal(1, FinishedRunCleanerWiring.SnapshotCount(db, run.Id));

        await using (var user = db.Fresh())
        {
            (await user.LoopRuns.SingleAsync(r => r.Id == run.Id)).Retain = false;
            await user.SaveChangesAsync();
        }
        db.Context.ChangeTracker.Clear();

        await BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 14).SweepOnceAsync(CancellationToken.None);

        var after = await ReadAsync(db, run.Id);
        Assert.NotNull(after);
        Assert.Null(after!.WorktreePath);
        Assert.Equal(0, FinishedRunCleanerWiring.SnapshotCount(db, run.Id));
    }

    private static (LoopTemplateVersion version, LoopTemplate template) SeedTemplate(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.SaveChanges();
        return (version, template);
    }

    private static LoopRun SeedRun(TestDb db, Guid versionId, string workItemId, LoopRunStatus status,
        DateTime? completedAt, string? worktree, string? branch, bool retain = false, DateTime? startedAt = null)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = versionId,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = status,
            CompletedAt = completedAt,
            StartedAt = startedAt,
            WorktreePath = worktree,
            BranchName = branch,
            Retain = retain,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    private static Guid AddRun(TestDb db, Guid versionId, string workItemId, DateTime completedAt)
    {
        var id = Guid.NewGuid();
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = id,
            WorkItemId = workItemId,
            LoopTemplateVersionId = versionId,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.Completed,
            CompletedAt = completedAt,
            WorktreePath = $"/tmp/wt/{id}",
            BranchName = $"ild/wi-x-run-{id}",
        });
        return id;
    }

    private static Mock<IWorkItemManager> WorkItemsReturning(params (string Id, RemoteWorkItemStatus Status)[] items)
    {
        var m = new Mock<IWorkItemManager>();
        foreach (var (id, status) in items)
            m.Setup(x => x.GetWorkItemAsync(id))
                .ReturnsAsync(new WorkItemView { Id = id, Status = status });
        return m;
    }

    private static Mock<IWorkItemManager> WorkItemsReturning(string workItemId, RemoteWorkItemStatus status)
    {
        var m = new Mock<IWorkItemManager>();
        m.Setup(x => x.GetWorkItemAsync(workItemId))
            .ReturnsAsync(new WorkItemView { Id = workItemId, Status = status });
        return m;
    }

    private static Mock<IRunReclaimer> ReclaimerReturning(bool success)
    {
        var m = new Mock<IRunReclaimer>();
        m.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(success);
        return m;
    }

    private static WorktreeRetentionSweeper BuildSweeper(
        TestDb db, IWorkItemManager workItems, IRunReclaimer reclaimer, int retentionDays, params Guid[] engineOwned)
    {
        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetRunRetentionDaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(retentionDays);
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(engineOwned);
        Mock.Get(workItems).Setup(x => x.DeleteRunAsync(It.IsAny<Guid>())).Returns<Guid>(db.LoopRuns.DeleteAsync);

        var services = new ServiceCollection();
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton<IAdapterSessionSnapshotStore>(new AdapterSessionSnapshotStore(db.Context));
        services.AddSingleton(workItems);
        services.AddSingleton(reclaimer);
        services.AddSingleton(settings.Object);
        services.AddSingleton(engine.Object);
        services.AddSingleton(TestActors.Instance);
        services.AddRealFinishedRunCleaner();
        return NewSweeper(services.BuildServiceProvider());
    }

    private static WorktreeRetentionSweeper NewSweeper(IServiceProvider provider)
        => ActivatorUtilities.CreateInstance<WorktreeRetentionSweeper>(provider);

    private static async Task<LoopRun?> ReadAsync(TestDb db, Guid runId)
    {
        await using var ctx = db.Fresh();
        return await ctx.LoopRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
    }
}
