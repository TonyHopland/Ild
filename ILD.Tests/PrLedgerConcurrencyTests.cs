using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The ledger is written by four things at once, and the heartbeat is the one
/// that takes its time: it decides what a run has been handed from a copy
/// loaded BEFORE a forge fetch that costs seconds, then writes at the end of
/// its tick. Anything recorded inside that window — most importantly a person
/// dropping a queued answer, which puts the finding back within reach — used to
/// be reverted by that write, leaving the finding suppressed and the promise
/// that dropping loses nothing false.
///
/// Against the real (SQLite) store: the defect is in what a write does to a
/// row, and a mocked store cannot show it.
/// </summary>
public class PrLedgerConcurrencyTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Body = "this allocation is wrong";

    private static readonly string Hash = PrCommentLedger.Fingerprint("src/A.cs", 10, Body);

    private static RemotePrReviewItem Item(string id)
        => new("review", id, $"PRRT_{id}", "r1", "src/A.cs", 10, Body, "Copilot", Head,
            new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Fetched(params RemotePrReviewItem[] items)
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", "body", Head, DateTime.UtcNow, "Copilot", false) },
            items, Head, null);

    /// <summary>A run parked at its PR node with the finding already handed over.</summary>
    private static async Task<LoopRun> SeedAsync(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        await db.Context.SaveChangesAsync();

        var delivered = PrCommentDelivery.Decide(Fetched(Item("11")), Head, PrCommentLedger.Empty with
        {
            Head = Head,
            WatchedFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        });

        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "wi-1",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.WaitingHuman,
            HumanFeedbackReason = HumanFeedbackReasons.PrAwaitingMerge,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            PrUrl = PrUrl,
            StartedAt = DateTime.UtcNow,
            PrCommentLedger = PrCommentLedgerJson.Serialize(delivered.Ledger),
        };
        await db.LoopRuns.CreateRunAsync(run);
        return run;
    }

    private static PrReviewService ServiceOn(TestDb db)
    {
        var remote = new Mock<IRemoteProvider>();
        remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(Fetched(Item("11")));
        remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);
        return new PrReviewService(new LoopRunStore(db.Fresh()), remote.Object);
    }

    /// <summary>Whether a later review restating the same finding would be handed over.</summary>
    private static async Task<bool> WouldRaiseAgainAsync(TestDb db, Guid runId)
    {
        var ledger = await new LoopRunStore(db.Fresh()).GetPrCommentLedgerAsync(runId);
        return PrCommentDelivery.Decide(Fetched(Item("99")), Head, PrCommentLedgerJson.TryParse(ledger)).Items.Count > 0;
    }

    [Fact]
    public async Task A_drop_inside_a_heartbeat_tick_is_not_reverted_by_it()
    {
        using var db = new TestDb();
        var run = await SeedAsync(db);
        var service = ServiceOn(db);
        var queued = await service.ReplyAsync("wi-1", "11", "Answered.", run.Id);
        Assert.True(queued.Ok);
        Assert.False(await WouldRaiseAgainAsync(db, run.Id));

        // The heartbeat is mid-tick: it loaded the run before going to the forge.
        var heartbeat = new LoopRunStore(db.Fresh());
        var carried = await heartbeat.GetByIdAsync(run.Id);
        Assert.NotNull(carried);

        // …the person drops the answer while that fetch is in flight…
        Assert.True(await service.DropQueuedAsync(run.Id, queued.Id!));

        // …and only now does the tick write its snapshot from the copy it holds.
        carried!.PrSnapshot = "{\"state\":\"open\"}";
        await heartbeat.UpdateRunAsync(carried);

        Assert.True(
            await WouldRaiseAgainAsync(db, run.Id),
            "the heartbeat's full-row write reverted the drop, so the finding stays suppressed");
        var after = await new LoopRunStore(db.Fresh()).GetByIdAsync(run.Id);
        Assert.Equal("{\"state\":\"open\"}", after!.PrSnapshot);
    }

    [Fact]
    public async Task A_drop_that_loses_the_race_to_a_ledger_write_is_re_applied()
    {
        // The other half: a targeted ledger write that lands between the drop's
        // read and its write must not swallow the drop either.
        using var db = new TestDb();
        var run = await SeedAsync(db);
        var service = ServiceOn(db);
        var queued = await service.ReplyAsync("wi-1", "11", "Answered.", run.Id);
        Assert.True(queued.Ok);

        var other = new LoopRunStore(db.Fresh());
        var current = await other.GetPrCommentLedgerAsync(run.Id);
        var moved = PrCommentLedgerJson.TryParse(current)! with { Head = Head, PostedIds = new[] { "review:5000" } };
        Assert.True(await other.TrySetPrCommentLedgerAsync(run.Id, current, PrCommentLedgerJson.Serialize(moved)));

        Assert.True(await service.DropQueuedAsync(run.Id, queued.Id!));

        Assert.True(await WouldRaiseAgainAsync(db, run.Id));
        // …without losing what the other writer had recorded.
        var after = PrCommentLedgerJson.TryParse(await new LoopRunStore(db.Fresh()).GetPrCommentLedgerAsync(run.Id));
        Assert.Contains("review:5000", after!.PostedIds);
    }
}
