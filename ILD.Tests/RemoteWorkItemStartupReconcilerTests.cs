using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ILD.Tests;

public class RemoteWorkItemStartupReconcilerTests
{
    [Fact]
    public async Task WaitingHuman_run_is_left_alive_so_heartbeats_resume_after_restart()
    {
        // A run parked at a Human/PR node has run.Status == WaitingHuman, not
        // Running. The scheduler derives its heartbeat set from the runs still
        // alive, so cancelling this one would stop the item being heartbeated —
        // the stale reclaimer would then flip it to Ready ~15 minutes after a
        // human resumes it and a second concurrent run gets claimed for the
        // same work item.
        var (db, run) = SeedRun(LoopRunStatus.WaitingHuman);
        using var _ = db;
        var recovery = new Mock<IRecoveryManager>();

        await RunReconcilerAsync(db, recovery,
            ServerReturns(run.WorkItemId, RemoteWorkItemStatus.HumanFeedback));

        recovery.Verify(r => r.RecoverRunAsync(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(LoopRunStatus.WaitingHuman, FreshStatus(db, run.Id));
    }

    [Fact]
    public async Task Running_item_is_recovered_through_the_recovery_manager()
    {
        var (db, run) = SeedRun(LoopRunStatus.Running);
        using var _ = db;
        var recovery = new Mock<IRecoveryManager>();

        await RunReconcilerAsync(db, recovery,
            ServerReturns(run.WorkItemId, RemoteWorkItemStatus.Running));

        // Via RecoveryManager (policy-aware), not a blind engine resume.
        recovery.Verify(r => r.RecoverRunAsync(run.Id), Times.Once);
        Assert.Equal(LoopRunStatus.Running, FreshStatus(db, run.Id));
    }

    [Fact]
    public async Task Server_reclaimed_item_cancels_the_local_run()
    {
        // The server flipped the item back to Ready (stale heartbeat while we
        // were down). It will be claimed as a fresh run — the orphaned local
        // Running run must be cancelled, or a later restart resurrects it, it
        // keeps holding a concurrency slot, and two loops fight over one work
        // item.
        var (db, run) = SeedRun(LoopRunStatus.Running);
        using var _ = db;
        var recovery = new Mock<IRecoveryManager>();

        await RunReconcilerAsync(db, recovery,
            ServerReturns(run.WorkItemId, RemoteWorkItemStatus.Ready));

        recovery.Verify(r => r.RecoverRunAsync(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(LoopRunStatus.Cancelled, FreshStatus(db, run.Id));
        Assert.NotNull(db.Fresh().LoopRuns.First(r => r.Id == run.Id).CompletedAt);
    }

    [Fact]
    public async Task Missing_server_item_cancels_the_local_run()
    {
        var (db, run) = SeedRun(LoopRunStatus.Running);
        using var _ = db;
        var recovery = new Mock<IRecoveryManager>();

        await RunReconcilerAsync(db, recovery, ServerReturns(run.WorkItemId, status: null));

        Assert.Equal(LoopRunStatus.Cancelled, FreshStatus(db, run.Id));
    }

    [Fact]
    public async Task Shutdown_halted_run_is_resumed_when_the_server_still_says_Running()
    {
        // The one WaitingHuman run with no pending signal coming: what it was
        // waiting for was this process starting again. Through the recovery
        // manager, so its policy and worktree health still get a say.
        var (db, run) = SeedRun(LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Shutdown);
        using var _ = db;
        var recovery = new Mock<IRecoveryManager>();

        await RunReconcilerAsync(db, recovery,
            ServerReturns(run.WorkItemId, RemoteWorkItemStatus.Running));

        recovery.Verify(r => r.RecoverRunAsync(run.Id), Times.Once);
    }

    [Fact]
    public async Task Human_halted_run_is_left_parked_even_when_the_server_says_Running()
    {
        // A person is waiting to steer it. The work item stays Running on the
        // server because the halt path parks the item in HumanFeedback and the
        // server may not have caught up — either way this is not ours to resume.
        var (db, run) = SeedRun(LoopRunStatus.WaitingHuman, isHalted: true, haltReason: null);
        using var _ = db;
        var recovery = new Mock<IRecoveryManager>();

        await RunReconcilerAsync(db, recovery,
            ServerReturns(run.WorkItemId, RemoteWorkItemStatus.Running));

        recovery.Verify(r => r.RecoverRunAsync(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(LoopRunStatus.WaitingHuman, FreshStatus(db, run.Id));
    }

    [Fact]
    public async Task Shutdown_halted_run_whose_item_the_server_reclaimed_is_cancelled_not_resumed()
    {
        // The item is being handed out as a fresh run. Resuming ours would put
        // two loops on one work item — the drain's park does not change that.
        var (db, run) = SeedRun(LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Shutdown);
        using var _ = db;
        var recovery = new Mock<IRecoveryManager>();

        await RunReconcilerAsync(db, recovery,
            ServerReturns(run.WorkItemId, RemoteWorkItemStatus.Ready));

        recovery.Verify(r => r.RecoverRunAsync(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(LoopRunStatus.Cancelled, FreshStatus(db, run.Id));
    }

    // ----- plumbing -----

    private static (TestDb db, LoopRun run) SeedRun(
        LoopRunStatus status, bool isHalted = false, HaltReason? haltReason = null)
    {
        var db = new TestDb();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = $"WI-{Guid.NewGuid():N}",
            LoopTemplateVersionId = version.Id,
            Status = status,
            StartedAt = DateTime.UtcNow,
            IsHalted = isHalted,
            HaltReason = haltReason,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return (db, run);
    }

    private static Mock<IWorkItemServerClient> ServerReturns(string workItemId, RemoteWorkItemStatus? status)
    {
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.GetAsync(It.IsAny<WorkItemServerOptions>(), workItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status is null
                ? null
                : new RemoteWorkItem { Id = workItemId, Status = status.Value });
        return client;
    }

    private static LoopRunStatus FreshStatus(TestDb db, Guid runId)
        => db.Fresh().LoopRuns.First(r => r.Id == runId).Status;

    /// <summary>
    /// An engine whose <c>StopRunAsync</c> ends the row the way the real one
    /// does — terminal status, completion timestamp, reason — so these cases
    /// keep asserting the outcome the reconciler is responsible for rather than
    /// the call it makes to get there.
    /// </summary>
    private static Mock<ILoopEngine> EngineEndingRuns(TestDb db)
    {
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.StopRunAsync(It.IsAny<Guid>(), It.IsAny<string>()))
              .Returns(async (Guid runId, string reason) =>
              {
                  using var fresh = db.Fresh();
                  var r = fresh.LoopRuns.First(x => x.Id == runId);
                  r.Status = LoopRunStatus.Cancelled;
                  r.CompletedAt ??= DateTime.UtcNow;
                  r.HumanFeedbackReason = reason;
                  await fresh.SaveChangesAsync();
                  return (string?)r.WorkItemId;
              });
        return engine;
    }

    private static async Task RunReconcilerAsync(
        TestDb db,
        Mock<IRecoveryManager> recovery,
        Mock<IWorkItemServerClient> client,
        Mock<ILoopEngine>? engine = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(recovery.Object);
        services.AddSingleton(client.Object);
        services.AddSingleton((engine ?? EngineEndingRuns(db)).Object);
        using var sp = services.BuildServiceProvider();

        var options = new Mock<IOptionsMonitor<WorkItemSchedulerOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new WorkItemSchedulerOptions
        {
            Enabled = true,
            BaseUrl = "http://server",
            ApiKey = "key",
        });

        var reconciler = new RemoteWorkItemStartupReconciler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options.Object,
            NullLogger<RemoteWorkItemStartupReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);
    }
}
