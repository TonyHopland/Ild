using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The opt-in retry of a Provider Interruption park. Two properties matter more
/// than the retry itself: with <c>throttle.autoResume</c> off nothing here ever
/// runs, and with it on only a <see cref="HaltReason.Throttled"/> park is
/// eligible — a human's Halt, a shutdown park and a traversal-cap park are all
/// somebody else's to resume.
/// </summary>
public class ThrottledRunResumeSweeperTests
{
    private static readonly TimeSpan PastTheGrace = TimeSpan.FromMinutes(11);

    [Fact]
    public async Task Does_nothing_while_the_setting_is_off()
    {
        using var db = new TestDb();
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace);
        var engine = new Mock<ILoopEngine>();

        // No key written at all: the default is what a fresh install runs with,
        // and it must leave the park exactly as manual as it has always been.
        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(0, Reload(db, run.Id).ThrottleAutoResumeCount);
    }

    [Fact]
    public async Task Does_nothing_when_the_setting_is_turned_off()
    {
        using var db = new TestDb();
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace);
        await db.Settings.UpsertAsync(AppSettingKeys.ThrottleAutoResume, "false");
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Resumes_a_throttle_park_that_is_past_the_grace_period()
    {
        using var db = new TestDb();
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        // The note is what tells the agent — and the event log — that ILD picked
        // the run back up rather than a person, and `automatic` is what stops the
        // resume refilling the AI traversal budget it did not earn.
        engine.Verify(e => e.ResumeFromHaltAsync(
            run.Id, ThrottledRunResumeSweeper.AutomaticResumeNote, true), Times.Once);
    }

    [Fact]
    public async Task Leaves_a_freshly_parked_run_alone()
    {
        using var db = new TestDb();
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow.AddMinutes(-1));
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        // A provider that has just said "not now" will still say it a minute
        // later; retrying immediately spends a round trip to be told again.
        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(HaltReason.Human)]
    [InlineData(HaltReason.Shutdown)]
    [InlineData(HaltReason.MaxAiTraversals)]
    public async Task Never_resumes_a_park_somebody_else_owns(HaltReason reason)
    {
        using var db = new TestDb();
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace, haltReason: reason);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Never_resumes_a_shutdown_halted_run()
    {
        using var db = new TestDb();
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace,
            haltReason: HaltReason.Shutdown);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        // Startup owns this park (ADR-0017); resuming it here would race the
        // recovery paths that resume it against the same agent session.
        Assert.True(Reload(db, run.Id).IsShutdownHalted);
        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Leaves_a_paused_run_alone()
    {
        using var db = new TestDb();
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace, isPaused: true);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Stops_after_the_bound_and_leaves_the_run_for_a_person()
    {
        using var db = new TestDb();
        // Five attempts spent, and the run has been parked far longer than even
        // the last (doubled) delay — the only thing holding it back is the bound.
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow.AddDays(-1), autoResumeCount: 5);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Backs_off_further_with_every_attempt_already_spent()
    {
        using var db = new TestDb();
        // 40 minutes parked: past the 10-minute first delay and the 20-minute
        // second, but not the 80 minutes a run on its fourth attempt waits.
        var parkedAt = DateTime.UtcNow.AddMinutes(-40);
        var due = SeedThrottleParkedRun(db, parkedAt, autoResumeCount: 1);
        var notDue = SeedThrottleParkedRun(db, parkedAt, autoResumeCount: 3);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(due.Id, It.IsAny<string?>(), true), Times.Once);
        engine.Verify(e => e.ResumeFromHaltAsync(notDue.Id, It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task One_failing_resume_does_not_strand_the_rest_of_the_sweep()
    {
        using var db = new TestDb();
        var first = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace);
        var second = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheGrace);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.ResumeFromHaltAsync(first.Id, It.IsAny<string?>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("provider unreachable"));

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(second.Id, It.IsAny<string?>(), true), Times.Once);
    }

    private static Task EnableAsync(TestDb db)
        => db.Settings.UpsertAsync(AppSettingKeys.ThrottleAutoResume, "true");

    private static LoopRun Reload(TestDb db, Guid runId)
        => db.Fresh().LoopRuns.First(r => r.Id == runId);

    private static LoopRun SeedThrottleParkedRun(
        TestDb db,
        DateTime parkedAt,
        HaltReason haltReason = HaltReason.Throttled,
        bool isPaused = false,
        int autoResumeCount = 0)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            Status = LoopRunStatus.WaitingHuman,
            IsHalted = true,
            HaltReason = haltReason,
            HumanFeedbackReason = HumanFeedbackReasons.AiProviderThrottled,
            IsPaused = isPaused,
            ThrottleAutoResumeCount = autoResumeCount,
            StartedAt = parkedAt,
            CreatedAt = parkedAt,
            // TouchUpdatedAt only stamps Modified entries, so this explicit park
            // time survives the initial Add+SaveChanges.
            UpdatedAt = parkedAt,
        };
        db.Context.LoopRuns.Add(run);
        db.Context.SaveChanges();
        return run;
    }

    private static Task SweepAsync(TestDb db, ILoopEngine engine)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db.LoopRuns);
        services.AddSingleton<ISchedulerSettingsService>(new SchedulerSettingsService(db.Settings));
        var provider = services.BuildServiceProvider();
        var sweeper = new ThrottledRunResumeSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(), engine,
            NullLogger<ThrottledRunResumeSweeper>.Instance);

        return (Task)typeof(ThrottledRunResumeSweeper)
            .GetMethod("SweepOnceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(sweeper, new object?[] { CancellationToken.None })!;
    }
}
