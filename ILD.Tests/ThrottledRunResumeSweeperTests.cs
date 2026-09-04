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
    /// <summary>Past the default hour between attempts, whatever else a case sets.</summary>
    private static readonly TimeSpan PastTheDelay = TimeSpan.FromMinutes(61);

    [Fact]
    public async Task Does_nothing_while_the_setting_is_off()
    {
        using var db = new TestDb();
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay);
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
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay);
        await db.Settings.UpsertAsync(AppSettingKeys.ThrottleAutoResume, "false");
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Resumes_a_throttle_park_once_the_delay_has_elapsed()
    {
        using var db = new TestDb();
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay);
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
    public async Task Leaves_a_run_parked_inside_the_default_hour_alone()
    {
        using var db = new TestDb();
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow.AddMinutes(-30));
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        // Nothing configured, so the default hour applies: a provider that said
        // "not now" half an hour ago will mostly still say it.
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
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay, haltReason: reason);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Never_resumes_a_shutdown_halted_run()
    {
        using var db = new TestDb();
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay,
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
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay, isPaused: true);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Stops_after_the_default_six_retries_and_leaves_the_run_for_a_person()
    {
        using var db = new TestDb();
        // Six automatic resumes spent since anyone touched this run, and it has
        // been parked far longer than the delay — the only thing holding it back
        // is the bound, and only a human clears that.
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow.AddDays(-1), autoResumeCount: 6);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Waits_the_configured_delay_between_attempts()
    {
        using var db = new TestDb();
        // The gap is the operator's number and stays that number however many
        // attempts have been spent — it does not widen underneath them.
        var parkedAt = DateTime.UtcNow.AddMinutes(-6);
        var due = SeedThrottleParkedRun(db, parkedAt, autoResumeCount: 3);
        var notDue = SeedThrottleParkedRun(db, DateTime.UtcNow.AddMinutes(-2));
        await EnableAsync(db, retryDelayMinutes: 5);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(due.Id, It.IsAny<string?>(), true), Times.Once);
        engine.Verify(e => e.ResumeFromHaltAsync(notDue.Id, It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Stops_after_the_configured_number_of_retries()
    {
        using var db = new TestDb();
        var parkedAt = DateTime.UtcNow - PastTheDelay;
        var stillAllowed = SeedThrottleParkedRun(db, parkedAt, autoResumeCount: 1);
        var spent = SeedThrottleParkedRun(db, parkedAt, autoResumeCount: 2);
        await EnableAsync(db, maxRetries: 2);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(stillAllowed.Id, It.IsAny<string?>(), true), Times.Once);
        engine.Verify(e => e.ResumeFromHaltAsync(spent.Id, It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task A_shortened_delay_applies_to_a_run_already_parked()
    {
        using var db = new TestDb();
        // Read per sweep: an operator who decides an hour is too long should not
        // have to wait out the old number on the runs already waiting.
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow.AddMinutes(-10));
        await EnableAsync(db, retryDelayMinutes: 5);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(run.Id, It.IsAny<string?>(), true), Times.Once);
    }

    [Fact]
    public async Task Waits_for_the_reset_time_the_provider_stated()
    {
        using var db = new TestDb();
        // Past the backoff, but the provider said the limit lifts in another
        // half hour. Spending an attempt now buys a refusal we were told about.
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay,
            resetAt: DateTime.UtcNow.AddMinutes(30));
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Resumes_once_the_stated_reset_time_has_passed()
    {
        using var db = new TestDb();
        var run = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay,
            resetAt: DateTime.UtcNow.AddMinutes(-1));
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(run.Id, ThrottledRunResumeSweeper.AutomaticResumeNote, true), Times.Once);
    }

    [Fact]
    public async Task A_passed_reset_time_does_not_shortcut_the_backoff()
    {
        using var db = new TestDb();
        // The reset is long gone but the run was only just parked: the deadline
        // is a floor under the backoff, never a trigger of its own.
        SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow.AddMinutes(-1),
            resetAt: DateTime.UtcNow.AddHours(-2));
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task One_failing_resume_does_not_strand_the_rest_of_the_sweep()
    {
        using var db = new TestDb();
        var first = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay);
        var second = SeedThrottleParkedRun(db, parkedAt: DateTime.UtcNow - PastTheDelay);
        await EnableAsync(db);
        var engine = new Mock<ILoopEngine>();
        engine.Setup(e => e.ResumeFromHaltAsync(first.Id, It.IsAny<string?>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("provider unreachable"));

        await SweepAsync(db, engine.Object);

        engine.Verify(e => e.ResumeFromHaltAsync(second.Id, It.IsAny<string?>(), true), Times.Once);
    }

    private static async Task EnableAsync(TestDb db, int? retryDelayMinutes = null, int? maxRetries = null)
    {
        await db.Settings.UpsertAsync(AppSettingKeys.ThrottleAutoResume, "true");
        if (retryDelayMinutes is int mins)
            await db.Settings.UpsertAsync(AppSettingKeys.ThrottleRetryDelayMinutes, mins.ToString());
        if (maxRetries is int retries)
            await db.Settings.UpsertAsync(AppSettingKeys.ThrottleMaxRetries, retries.ToString());
    }

    private static LoopRun Reload(TestDb db, Guid runId)
        => db.Fresh().LoopRuns.First(r => r.Id == runId);

    private static LoopRun SeedThrottleParkedRun(
        TestDb db,
        DateTime parkedAt,
        HaltReason haltReason = HaltReason.Throttled,
        bool isPaused = false,
        int autoResumeCount = 0,
        DateTime? resetAt = null)
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
            ThrottleResetAt = resetAt,
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
