using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class StuckRunWatchdogTests
{
    [Fact]
    public async Task Recovers_run_running_with_no_live_driver()
    {
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var run = SeedRun(db, version.Id, LoopRunStatus.Running,
            updatedAt: DateTime.UtcNow.AddMinutes(-10));

        var engine = EngineWithActiveRuns(/* none */);
        var recovery = RecoveryReturning(true);

        await InvokeSweepOnceAsync(BuildWatchdog(db, engine.Object, recovery.Object));

        recovery.Verify(r => r.RecoverRunAsync(run.Id), Times.Once);
    }

    [Fact]
    public async Task Never_touches_a_live_long_running_job()
    {
        // The key safety property: a job with a live driving task is exempt no
        // matter how long it has been running (stale UpdatedAt below).
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        var run = SeedRun(db, version.Id, LoopRunStatus.Running,
            updatedAt: DateTime.UtcNow.AddHours(-5));

        var engine = EngineWithActiveRuns(run.Id); // a task is driving it
        var recovery = RecoveryReturning(true);

        await InvokeSweepOnceAsync(BuildWatchdog(db, engine.Object, recovery.Object));

        recovery.Verify(r => r.RecoverRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Skips_run_within_launch_grace_window()
    {
        // Running, no driver yet, but the row was just written — this is the
        // sub-second window between a status write and its task registering, not
        // a stuck run. Must not be recovered.
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        SeedRun(db, version.Id, LoopRunStatus.Running, updatedAt: DateTime.UtcNow);

        var engine = EngineWithActiveRuns(/* none */);
        var recovery = RecoveryReturning(true);

        await InvokeSweepOnceAsync(BuildWatchdog(db, engine.Object, recovery.Object));

        recovery.Verify(r => r.RecoverRunAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Skips_paused_run()
    {
        var db = new TestDb();
        var (version, _) = SeedTemplate(db);
        SeedRun(db, version.Id, LoopRunStatus.Running,
            updatedAt: DateTime.UtcNow.AddMinutes(-10), isPaused: true);

        var engine = EngineWithActiveRuns(/* none */);
        var recovery = RecoveryReturning(true);

        await InvokeSweepOnceAsync(BuildWatchdog(db, engine.Object, recovery.Object));

        recovery.Verify(r => r.RecoverRunAsync(It.IsAny<Guid>()), Times.Never);
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

    private static LoopRun SeedRun(TestDb db, Guid versionId, LoopRunStatus status,
        DateTime updatedAt, bool isPaused = false)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = versionId,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = status,
            IsPaused = isPaused,
            StartedAt = updatedAt,
            CreatedAt = updatedAt,
            // TouchUpdatedAt only stamps Modified entries, so this explicit value
            // survives the initial Add+SaveChanges.
            UpdatedAt = updatedAt,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    private static Mock<ILoopEngine> EngineWithActiveRuns(params Guid[] active)
    {
        var m = new Mock<ILoopEngine>();
        m.Setup(e => e.GetActiveRunIdsAsync()).ReturnsAsync(active);
        return m;
    }

    private static Mock<IRecoveryManager> RecoveryReturning(bool result)
    {
        var m = new Mock<IRecoveryManager>();
        m.Setup(r => r.RecoverRunAsync(It.IsAny<Guid>())).ReturnsAsync(result);
        return m;
    }

    private static StuckRunWatchdog BuildWatchdog(TestDb db, ILoopEngine engine, IRecoveryManager recovery)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoopRunStore>(db.LoopRuns);
        services.AddSingleton(recovery);
        var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        return new StuckRunWatchdog(scopes, engine, NullLogger<StuckRunWatchdog>.Instance);
    }

    private static Task InvokeSweepOnceAsync(StuckRunWatchdog watchdog) =>
        (Task)typeof(StuckRunWatchdog)
            .GetMethod("SweepOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(watchdog, new object?[] { CancellationToken.None })!;
}
