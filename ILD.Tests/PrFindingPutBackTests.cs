using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// "Dropping loses nothing" has to survive the throttles. The heartbeat marks a
/// finding delivered when it hands it to the round, so an answer that never
/// reaches the pull request — dropped by a person, or refused by the forge —
/// would otherwise leave that finding retired: the thread stays open and no
/// later review ever raises it again.
///
/// Only the CONTENT is forgotten, never the comment id. Forgetting the id would
/// let the same comment fire on the very next tick, and a forge that keeps
/// refusing would earn an answer, a refusal and another firing every minute.
/// </summary>
public class PrFindingPutBackTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Body = "this allocation is wrong";

    private static RemotePrReviewItem Item(string id, string body = Body)
        => new("review", id, $"PRRT_{id}", "r1", "src/A.cs", 10, body, "Copilot", Head,
            new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Fetched(params RemotePrReviewItem[] items)
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", "body", Head, DateTime.UtcNow, "Copilot", false) },
            items, Head, null);

    private sealed class Harness
    {
        public LoopRun Run { get; }
        public Mock<ILoopRunStore> Runs { get; } = new();
        public Mock<IRemoteProvider> Remote { get; } = new();

        public Harness()
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "wi-1",
                PrUrl = PrUrl,
                Status = LoopRunStatus.Running,
            };
            Runs.Setup(s => s.GetCurrentByWorkItemAsync("wi-1")).ReturnsAsync(Run);
            Runs.Setup(s => s.GetByIdAsync(Run.Id)).ReturnsAsync(() => Run);
            Runs.Setup(s => s.GetPrCommentQueueAsync(Run.Id)).ReturnsAsync(() => Run.PrCommentQueue);
            Runs.Setup(s => s.TrySetPrCommentQueueAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentQueue, StringComparison.Ordinal)) return false;
                    Run.PrCommentQueue = json;
                    return true;
                });
            Runs.Setup(s => s.GetPrCommentLedgerAsync(Run.Id)).ReturnsAsync(() => Run.PrCommentLedger);
            Runs.Setup(s => s.TrySetPrCommentLedgerAsync(Run.Id, It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid _, string? expected, string? json) =>
                {
                    if (!string.Equals(expected, Run.PrCommentLedger, StringComparison.Ordinal)) return false;
                    Run.PrCommentLedger = json;
                    return true;
                });
            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(Fetched(Item("11")));
            Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(true);
        }

        /// <summary>The state after the heartbeat handed the finding to the round.</summary>
        public void AfterDelivery()
        {
            var decision = PrCommentDelivery.Decide(Fetched(Item("11")), Head, PrCommentLedger.Empty with
            {
                Head = Head,
                WatchedFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
            });
            Run.PrCommentLedger = PrCommentLedgerJson.Serialize(decision.Ledger);
        }

        public PrReviewService Build() => new(Runs.Object, Remote.Object);

        /// <summary>What a later review restating the same finding under a fresh id would deliver.</summary>
        public IReadOnlyList<RemotePrReviewItem> WouldFireFor(RemotePrReviewItem restated)
            => PrCommentDelivery.Decide(
                Fetched(restated), Head, PrCommentLedgerJson.TryParse(Run.PrCommentLedger)).Items;
    }

    [Fact]
    public async Task A_dropped_answer_lets_a_later_review_raise_the_same_finding_again()
    {
        var h = new Harness();
        h.AfterDelivery();
        var service = h.Build();
        var queued = await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id);
        Assert.True(queued.Ok);
        // Before the drop the restated finding is swallowed as already answered.
        Assert.Empty(h.WouldFireFor(Item("99")));

        Assert.True(await service.DropQueuedAsync(h.Run.Id, queued.Id!));

        var raised = Assert.Single(h.WouldFireFor(Item("99")));
        Assert.Equal("99", raised.CommentId);
    }

    [Fact]
    public async Task The_comment_already_handed_over_still_never_fires_twice()
    {
        // The other half of the promise: putting the finding back must not put
        // the same comment back, or the round answers it, the human drops it,
        // and the loop offers it again on the next tick for ever.
        var h = new Harness();
        h.AfterDelivery();
        var service = h.Build();
        var queued = await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id);

        Assert.True(await service.DropQueuedAsync(h.Run.Id, queued.Id!));

        Assert.Empty(h.WouldFireFor(Item("11")));
    }

    [Fact]
    public async Task A_finding_restated_in_different_words_was_never_suppressed_anyway()
    {
        var h = new Harness();
        h.AfterDelivery();

        Assert.Single(h.WouldFireFor(Item("99", "a different objection entirely")));
    }

    [Fact]
    public async Task A_drop_that_matches_nothing_leaves_the_ledger_alone()
    {
        var h = new Harness();
        h.AfterDelivery();
        var before = h.Run.PrCommentLedger;
        var service = h.Build();
        Assert.True((await service.ReplyAsync("wi-1", "11", "Answered.", h.Run.Id)).Ok);

        Assert.False(await service.DropQueuedAsync(h.Run.Id, "no-such-write"));

        Assert.Equal(before, h.Run.PrCommentLedger);
    }

    [Fact]
    public async Task A_queue_written_before_this_existed_drops_without_touching_the_ledger()
    {
        // SourceHash is absent on an intent queued by an older build; the drop
        // must still work rather than throw or clear something at random.
        var h = new Harness();
        h.AfterDelivery();
        var before = h.Run.PrCommentLedger;
        h.Run.PrCommentQueue = PrCommentQueueJson.Serialize(new[]
        {
            new PrQueuedWrite("w1", PrQueuedWrite.Reply, "11", "Answered.", "src/A.cs", 10, DateTime.UtcNow),
        });

        Assert.True(await h.Build().DropQueuedAsync(h.Run.Id, "w1"));

        Assert.Null(h.Run.PrCommentQueue);
        Assert.Equal(before, h.Run.PrCommentLedger);
    }
}
