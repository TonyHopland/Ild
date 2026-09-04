using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The two queries the timer-driven sweeps run: the stuck-run watchdog's and
/// the throttle resume sweeper's. Both ask the database for exactly the runs
/// they act on, so what they select is now SQL rather than a filter the caller
/// applies — and both run every minute for the life of the process, so both
/// have to come back untracked.
/// </summary>
public class LoopRunStoreSweepQueryTests
{
    [Fact]
    public async Task Recoverable_finds_a_crashed_run_and_a_drained_one_and_nothing_else()
    {
        using var db = new TestDb();
        var crashed = await SeedAsync(db, LoopRunStatus.Running);
        var drained = await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Shutdown);
        // Somebody else's to resume: a person's Halt, a throttle park, the
        // traversal cap, and an ordinary park at a Human/PR node.
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true);
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Throttled);
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.MaxAiTraversals);
        await SeedAsync(db, LoopRunStatus.WaitingHuman);
        await SeedAsync(db, LoopRunStatus.Completed);

        var found = await new LoopRunStore(db.Fresh()).GetRecoverableRunsAsync();

        Assert.Equal(
            new[] { crashed, drained }.OrderBy(id => id),
            found.Select(r => r.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task Throttle_parked_finds_only_a_throttle_park_nobody_has_paused()
    {
        using var db = new TestDb();
        var throttled = await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Throttled);
        // A person paused this one on top of the park; that is a second answer
        // of "not now" and outranks the retry.
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Throttled, isPaused: true);
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Shutdown);
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true);
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.MaxAiTraversals);
        // Throttled once, but resumed since: not parked any more.
        await SeedAsync(db, LoopRunStatus.Running, haltReason: HaltReason.Throttled);

        var found = await new LoopRunStore(db.Fresh()).GetThrottleParkedRunsAsync();

        Assert.Equal(throttled, Assert.Single(found).Id);
    }

    [Fact]
    public async Task Both_sweeps_read_without_tracking_what_they_read()
    {
        // A sweep that acts on nothing must leave nothing behind: these run once
        // a minute forever, and a tracked entity per parked run per pass is the
        // cost the narrow queries exist to avoid.
        using var db = new TestDb();
        await SeedAsync(db, LoopRunStatus.Running);
        await SeedAsync(db, LoopRunStatus.WaitingHuman, isHalted: true, haltReason: HaltReason.Throttled);

        var ctx = db.Fresh();
        var store = new LoopRunStore(ctx);
        await store.GetRecoverableRunsAsync();
        await store.GetThrottleParkedRunsAsync();

        Assert.Empty(ctx.ChangeTracker.Entries<LoopRun>());
    }

    [Fact]
    public async Task A_run_read_by_a_sweep_can_still_be_written_back()
    {
        // The watchdog heals what it finds, so an untracked read has to survive
        // the round trip through UpdateRunAsync, which attaches it.
        using var db = new TestDb();
        var id = await SeedAsync(db, LoopRunStatus.Running);

        var store = new LoopRunStore(db.Fresh());
        var run = Assert.Single(await store.GetRecoverableRunsAsync());
        run.Status = LoopRunStatus.Failed;
        run.HumanFeedbackReason = HumanFeedbackReasons.RunCrashed;
        await store.UpdateRunAsync(run);

        var reloaded = db.Fresh().LoopRuns.AsNoTracking().First(r => r.Id == id);
        Assert.Equal(LoopRunStatus.Failed, reloaded.Status);
        Assert.Equal(HumanFeedbackReasons.RunCrashed, reloaded.HumanFeedbackReason);
    }

    private static async Task<Guid> SeedAsync(
        TestDb db,
        LoopRunStatus status,
        bool isHalted = false,
        HaltReason? haltReason = null,
        bool isPaused = false)
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
            Status = status,
            IsHalted = isHalted,
            HaltReason = haltReason,
            IsPaused = isPaused,
        };
        db.Context.LoopRuns.Add(run);
        await db.Context.SaveChangesAsync();
        return run.Id;
    }
}
