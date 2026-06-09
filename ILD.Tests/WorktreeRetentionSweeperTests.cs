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
        var repo = new Mock<IRepositoryManager>();
        repo.Setup(r => r.ResolveBaseRepoPathAsync("/tmp/wt/run-a")).ReturnsAsync("/repos/x");

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, repo.Object, retentionDays: 30));

        repo.Verify(r => r.DestroyWorktreeAsync("/tmp/wt/run-a"), Times.Once);
        repo.Verify(r => r.DeleteLocalBranchAsync("/repos/x", "ild/wi-x-run-a"), Times.Once);
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
        var repo = new Mock<IRepositoryManager>();

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, repo.Object, retentionDays: 30));

        repo.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never);
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
        var repo = new Mock<IRepositoryManager>();

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, repo.Object, retentionDays: 0));

        repo.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never);
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
        var repo = new Mock<IRepositoryManager>();

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, repo.Object, retentionDays: 30));

        repo.Verify(r => r.DestroyWorktreeAsync(It.IsAny<string>()), Times.Never);
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
        var repo = new Mock<IRepositoryManager>();
        repo.Setup(r => r.ResolveBaseRepoPathAsync("/tmp/wt/old")).ReturnsAsync("/repos/x");

        await InvokeSweepOnceAsync(BuildSweeper(db, workItems.Object, repo.Object, retentionDays: 30));

        repo.Verify(r => r.DestroyWorktreeAsync("/tmp/wt/old"), Times.Once);
        Assert.Null(await db.LoopRuns.GetByIdAsync(old.Id));
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

    private static WorktreeRetentionSweeper BuildSweeper(TestDb db, IWorkItemManager workItems, IRepositoryManager repo, int retentionDays)
    {
        var settings = new Mock<ISchedulerSettingsService>();
        settings.Setup(s => s.GetRunRetentionDaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(retentionDays);

        var services = new ServiceCollection();
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(workItems);
        services.AddSingleton(repo);
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
