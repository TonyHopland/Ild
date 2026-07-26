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

    // ----- plumbing -----

    private static (TestDb db, LoopRun run) SeedRun(LoopRunStatus status)
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
