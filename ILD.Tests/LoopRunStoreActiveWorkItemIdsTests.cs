using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;

namespace ILD.Tests;

/// <summary>
/// The Active Work Item Set is what the scheduler heartbeats and counts against
/// its concurrency cap, and this query is the only definition of it. A run
/// status wrongly counted as alive burns a slot forever; one wrongly counted as
/// dead drops the item out of the heartbeat and lets the server hand it to a
/// second concurrent run.
/// </summary>
public class LoopRunStoreActiveWorkItemIdsTests
{
    [Fact]
    public async Task Reports_running_and_waitinghuman_runs_and_nothing_else()
    {
        using var db = new TestDb();

        var running = await SeedRunAsync(db, LoopRunStatus.Running);
        var parked = await SeedRunAsync(db, LoopRunStatus.WaitingHuman);
        await SeedRunAsync(db, LoopRunStatus.Completed);
        await SeedRunAsync(db, LoopRunStatus.Failed);
        await SeedRunAsync(db, LoopRunStatus.Cancelled);

        var ids = await new LoopRunStore(db.Fresh()).GetActiveWorkItemIdsAsync();

        Assert.Equal(
            new[] { running, parked }.OrderBy(x => x, StringComparer.Ordinal),
            ids.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reports_a_work_item_once_however_many_live_runs_it_has()
    {
        // The set is per work item, not per run: a duplicate would count twice
        // against the cap and be heartbeated twice.
        using var db = new TestDb();

        var wi = $"WI-{Guid.NewGuid():N}";
        await SeedRunAsync(db, LoopRunStatus.Running, wi);
        await SeedRunAsync(db, LoopRunStatus.WaitingHuman, wi);

        var ids = await new LoopRunStore(db.Fresh()).GetActiveWorkItemIdsAsync();

        Assert.Equal(new[] { wi }, ids);
    }

    [Fact]
    public async Task Skips_runs_with_no_work_item()
    {
        // WorkItemId defaults to empty rather than null, and an empty id in the
        // heartbeat is a request the server can only ignore.
        using var db = new TestDb();

        var real = await SeedRunAsync(db, LoopRunStatus.Running);
        await SeedRunAsync(db, LoopRunStatus.Running, workItemId: "");

        var ids = await new LoopRunStore(db.Fresh()).GetActiveWorkItemIdsAsync();

        Assert.Equal(new[] { real }, ids);
    }

    [Fact]
    public async Task Does_not_track_the_runs_it_reads()
    {
        // Reading the set must not cost a row in the change tracker: the
        // scheduler reads it at the top of every pass, and a tracked run would
        // make a later re-read inside that pass resolve to the top-of-pass
        // snapshot instead of the database. Satisfying this through the
        // projection is what rules out filtering GetActiveRunsAsync instead.
        using var db = new TestDb();

        var wi = await SeedRunAsync(db, LoopRunStatus.Running);
        var ctx = db.Fresh();
        var store = new LoopRunStore(ctx);

        await store.GetActiveWorkItemIdsAsync();

        Assert.Empty(ctx.ChangeTracker.Entries<LoopRun>());
        Assert.Equal(new[] { wi }, await store.GetActiveWorkItemIdsAsync());
    }

    private static async Task<string> SeedRunAsync(TestDb db, LoopRunStatus status, string? workItemId = null)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"t-{Guid.NewGuid():N}" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);

        var id = workItemId ?? $"WI-{Guid.NewGuid():N}";
        db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = id,
            LoopTemplateVersionId = version.Id,
            Status = status,
            StartedAt = DateTime.UtcNow,
        });
        await db.Context.SaveChangesAsync();
        return id;
    }
}
