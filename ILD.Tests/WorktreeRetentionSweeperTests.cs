using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class WorktreeRetentionSweeperTests
{
    [Fact]
    public async Task Sweep_reclaims_old_terminal_run_for_done_workitem()
    {
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/run-a", branch: "ild/wi-x-run-a");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(true);

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 30));

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == run.Id)), Times.Once);
        Assert.Null(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    [Fact]
    public async Task Sweep_skips_pinned_run()
    {
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/pinned", branch: "ild/wi-x-run-pinned", retain: true);

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(true);

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 30));

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    [Fact]
    public async Task Sweep_disabled_when_retention_is_zero()
    {
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-400),
            worktree: "/tmp/wt/keep", branch: "ild/wi-x-run-keep");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(true);

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 0));

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    [Fact]
    public async Task Sweep_keeps_current_run_of_active_workitem()
    {
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        // A Failed run parked in HumanFeedback is the work item's current run.
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Failed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/active", branch: "ild/wi-x-run-active");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.HumanFeedback);
        var reclaimer = ReclaimerReturning(true);

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 30));

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>()), Times.Never);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));
    }

    [Fact]
    public async Task Sweep_reclaims_superseded_run_even_when_workitem_active()
    {
        var db = new TestDb();
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

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 30));

        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == old.Id)), Times.Once);
        Assert.Null(await db.LoopRuns.GetByIdAsync(old.Id));
    }

    [Fact]
    public async Task Sweep_keeps_run_row_when_reclaim_fails_so_next_sweep_retries()
    {
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var workItemId = Guid.NewGuid().ToString();
        var run = SeedRun(db, version.Id, workItemId, LoopRunStatus.Completed,
            completedAt: DateTime.UtcNow.AddDays(-40),
            worktree: "/tmp/wt/stuck", branch: "ild/wi-x-run-stuck");

        var workItems = WorkItemsReturning(workItemId, RemoteWorkItemStatus.Done);
        var reclaimer = ReclaimerReturning(false);

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, reclaimer.Object, retentionDays: 30));

        // Reclaim was attempted but reported failure: the row must survive so a
        // later sweep can retry — deleting it would orphan the worktree forever.
        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(x => x.Id == run.Id)), Times.Once);
        Assert.NotNull(await db.LoopRuns.GetByIdAsync(run.Id));

        // A later sweep where the reclaim succeeds deletes the row.
        var retryReclaimer = ReclaimerReturning(true);
        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, retryReclaimer.Object, retentionDays: 30));
        Assert.Null(await db.LoopRuns.GetByIdAsync(run.Id));
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
        DateTime? completedAt, string worktree, string branch, bool retain = false, DateTime? startedAt = null)
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

    private static WorktreeRetentionSweeper BuildSweeper(TestDb db, IWorkItemManager workItems, IRunReclaimer reclaimer, int retentionDays)
    {
        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetRunRetentionDaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(retentionDays);

        var services = new ServiceCollection();
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(workItems);
        services.AddSingleton(reclaimer);
        services.AddSingleton(settings.Object);
        var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        return new WorktreeRetentionSweeper(scopes, NullLogger<WorktreeRetentionSweeper>.Instance);
    }

    private static Task InvokeSweepOnceAsync(WorktreeRetentionSweeper sweeper) =>
        (Task)typeof(WorktreeRetentionSweeper)
            .GetMethod("SweepOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(sweeper, new object?[] { CancellationToken.None })!;
}
