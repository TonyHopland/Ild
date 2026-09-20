using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Dropping a queued write is the one place a human stops something becoming
/// public, so it has to survive being done twice at once. Read-modify-write on
/// the queue column does not: two drops each compute the list from before the
/// other, and the second to land puts back the item the first removed — which
/// the PR node then posts, after a person had explicitly stopped it.
///
/// Against the real (SQLite) store, because the defect is in what the write
/// does to a row and a mocked store cannot show that.
/// </summary>
public class PrWriteQueueConcurrencyTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";

    private static PrQueuedWrite Write(string id, string target)
        => new(id, PrQueuedWrite.Reply, target, $"answer for {target}", "src/A.cs", 10,
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));

    private static async Task<LoopRun> SeedAsync(TestDb db, params PrQueuedWrite[] queued)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        await db.Context.SaveChangesAsync();

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            PrUrl = PrUrl,
            StartedAt = DateTime.UtcNow,
            PrCommentQueue = PrCommentQueueJson.Serialize(queued),
        };
        await db.LoopRuns.CreateRunAsync(run);
        return run;
    }

    /// <summary>A service on its own store instance, as each HTTP request gets.</summary>
    private static PrReviewService ServiceOn(TestDb db)
        => new(new LoopRunStore(db.Fresh()), new Mock<IRemoteProvider>().Object);

    [Fact]
    public async Task Two_drops_at_once_both_take_effect()
    {
        using var db = new TestDb();
        var run = await SeedAsync(db, Write("w1", "101"), Write("w2", "102"), Write("w3", "103"));

        // Two people, two scopes, started before either has written.
        var first = ServiceOn(db);
        var second = ServiceOn(db);
        var results = await Task.WhenAll(
            first.DropQueuedAsync(run.Id, "w1"),
            second.DropQueuedAsync(run.Id, "w2"));

        Assert.Equal(new[] { true, true }, results);
        var left = PrCommentQueueJson.TryParse(await new LoopRunStore(db.Fresh()).GetPrCommentQueueAsync(run.Id));
        var remaining = Assert.Single(left);
        Assert.Equal("w3", remaining.Id);
    }

    [Fact]
    public async Task A_drop_that_lost_the_race_to_an_identical_drop_reports_it_is_gone()
    {
        using var db = new TestDb();
        var run = await SeedAsync(db, Write("w1", "101"), Write("w2", "102"));

        Assert.True(await ServiceOn(db).DropQueuedAsync(run.Id, "w1"));
        Assert.False(await ServiceOn(db).DropQueuedAsync(run.Id, "w1"));

        var left = Assert.Single(PrCommentQueueJson.TryParse(
            await new LoopRunStore(db.Fresh()).GetPrCommentQueueAsync(run.Id)));
        Assert.Equal("w2", left.Id);
    }

    [Fact]
    public async Task Dropping_the_last_one_leaves_no_queue_at_all()
    {
        using var db = new TestDb();
        var run = await SeedAsync(db, Write("w1", "101"));

        Assert.True(await ServiceOn(db).DropQueuedAsync(run.Id, "w1"));

        Assert.Null(await new LoopRunStore(db.Fresh()).GetPrCommentQueueAsync(run.Id));
    }

    [Fact]
    public async Task A_compare_and_set_against_a_stale_value_refuses()
    {
        using var db = new TestDb();
        var run = await SeedAsync(db, Write("w1", "101"));
        var store = new LoopRunStore(db.Fresh());
        var current = await store.GetPrCommentQueueAsync(run.Id);

        Assert.True(await store.TrySetPrCommentQueueAsync(run.Id, current, null));
        // The same caller trying again with what it read the first time.
        Assert.False(await store.TrySetPrCommentQueueAsync(run.Id, current, "[]"));
        Assert.Null(await store.GetPrCommentQueueAsync(run.Id));

        // …and null is a value a compare-and-set can match on, not a hole.
        Assert.True(await store.TrySetPrCommentQueueAsync(run.Id, null, "[]"));
        Assert.Equal("[]", await store.GetPrCommentQueueAsync(run.Id));
    }
}
