using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The round's own voice: what it says about itself, and what it says about an
/// item it read and chose not to answer.
///
/// The PR node used to write the first for it, from <c>prCommentTemplate</c>, on
/// every re-visit — so a round that had already answered on the threads
/// announced itself a second time carrying nothing. Three rounds of rules for
/// when to skip that comment each failed in one direction or the other, because
/// the question is not answerable from outside the round. It is answerable from
/// inside: a round with something to add calls <c>comment_on_pr</c>, a round
/// without says nothing, and an item that needed no answer is closed rather than
/// replied to with prose that says nothing.
/// </summary>
public class PrRoundVoiceTests
{
    private const string PrUrl = "https://github.com/team/repo/pull/7";
    private const string RepoUrl = "https://github.com/team/repo";
    private const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Finding = "this allocation is wrong";

    private static RemotePrReviewItem Inline(string id)
        => new("review", id, $"PRRT_{id}", "r1", "src/A.cs", 10, Finding, "Copilot", Head,
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
        public List<EventLog> Logged { get; } = new();

        public Harness(bool canResolve = true)
        {
            Run = new LoopRun
            {
                Id = Guid.NewGuid(), WorkItemId = "wi-1", PrUrl = PrUrl, Status = LoopRunStatus.Running,
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
            Remote.Setup(r => r.GetPullRequestReviewLedgerAsync(RepoUrl, "7")).ReturnsAsync(Fetched(Inline("11")));
            Remote.Setup(r => r.SupportsThreadResolutionAsync(RepoUrl)).ReturnsAsync(canResolve);

            Events.Setup(s => s.AppendAsync(It.IsAny<EventLog>()))
                .Callback<EventLog>(Logged.Add)
                .ReturnsAsync(1);
        }

        public Mock<IEventLogStore> Events { get; } = new();

        public PrReviewService Build() => new(Runs.Object, Remote.Object, null, Events.Object);

        public IReadOnlyList<PrQueuedWrite> Queue => PrCommentQueueJson.TryParse(Run.PrCommentQueue);
    }

    [Fact]
    public async Task A_round_with_something_general_to_say_queues_exactly_that()
    {
        var h = new Harness();

        var queued = await h.Build().CommentAsync("wi-1", "Rebased onto main and re-ran the gate.", h.Run.Id);

        Assert.True(queued.Ok);
        var write = Assert.Single(h.Queue);
        Assert.Equal(PrQueuedWrite.Comment, write.Kind);
        Assert.Equal("Rebased onto main and re-ran the gate.", write.Body);
        // No target and no finding behind it: it answers the round, not an item,
        // so dropping it has nothing to put back.
        Assert.Equal(string.Empty, write.TargetId);
        Assert.Null(write.SourceHash);
    }

    [Fact]
    public async Task A_comment_with_nothing_in_it_is_refused_rather_than_queued()
    {
        var h = new Harness();

        var refused = await h.Build().CommentAsync("wi-1", "   ", h.Run.Id);

        Assert.False(refused.Ok);
        Assert.Null(h.Run.PrCommentQueue);
    }

    [Fact]
    public async Task Closing_an_item_leaves_the_round_silent_on_the_pull_request()
    {
        // The whole point of it: the round considered the item and had nothing
        // to add, so nothing is written where a reviewer would read it.
        var h = new Harness();

        var closed = await h.Build().CloseAsync("wi-1", "11", resolve: false, h.Run.Id);

        Assert.True(closed.Ok);
        Assert.Null(h.Run.PrCommentQueue);
    }

    [Fact]
    public async Task Closing_an_item_says_so_where_a_person_can_read_it_afterwards()
    {
        // A close that resolves nothing writes nothing anywhere else, so without
        // this "considered and dismissed" is indistinguishable from never read.
        var h = new Harness();

        await h.Build().CloseAsync("wi-1", "11", resolve: false, h.Run.Id);

        var logged = Assert.Single(h.Logged);
        Assert.Equal(EventType.PrReviewItemClosed, logged.EventType);
        Assert.Equal(h.Run.Id, logged.LoopRunId);
        Assert.Contains("11", logged.Data!, StringComparison.Ordinal);
        Assert.Contains("src/A.cs:10", logged.Data!, StringComparison.Ordinal);
        Assert.Contains(Finding, logged.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_an_item_can_close_its_thread_as_well()
    {
        var h = new Harness();

        var closed = await h.Build().CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        Assert.True(closed.Ok);
        var write = Assert.Single(h.Queue);
        Assert.Equal(PrQueuedWrite.Resolve, write.Kind);
        Assert.Equal("PRRT_11", write.TargetId);
    }

    [Fact]
    public async Task A_forge_that_cannot_resolve_still_closes_the_item_and_says_the_thread_stayed_open()
    {
        var h = new Harness(canResolve: false);

        var closed = await h.Build().CloseAsync("wi-1", "11", resolve: true, h.Run.Id);

        Assert.True(closed.Ok);
        Assert.Contains("thread was left open", closed.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(h.Run.PrCommentQueue);
        Assert.Single(h.Logged);
    }

    [Fact]
    public async Task Closing_something_this_pull_request_does_not_hold_is_refused()
    {
        var h = new Harness();

        var refused = await h.Build().CloseAsync("wi-1", "no-such-id", resolve: false, h.Run.Id);

        Assert.False(refused.Ok);
        Assert.Empty(h.Logged);
    }

    [Fact]
    public async Task A_closed_item_is_not_handed_over_again_on_the_same_head()
    {
        // It was recorded as delivered when it was handed over, and closing does
        // not undo that — the round read it.
        var h = new Harness();
        var delivered = PrCommentDelivery.Decide(Fetched(Inline("11")), Head, PrCommentLedger.Empty with
        {
            Head = Head,
            WatchedFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        });
        h.Run.PrCommentLedger = PrCommentLedgerJson.Serialize(delivered.Ledger);

        await h.Build().CloseAsync("wi-1", "11", resolve: false, h.Run.Id);

        Assert.Empty(PrCommentDelivery.Decide(
            Fetched(Inline("11")), Head, PrCommentLedgerJson.TryParse(h.Run.PrCommentLedger)).Items);
    }

    [Fact]
    public async Task A_closed_finding_restated_against_new_code_is_handed_over_again()
    {
        // The judgement was made against the code as it stood. On a new head it
        // stops being safe, which is exactly when the fingerprints are dropped —
        // closing must not add a suppression that outlives them.
        const string moved = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var h = new Harness();
        var delivered = PrCommentDelivery.Decide(Fetched(Inline("11")), Head, PrCommentLedger.Empty with
        {
            Head = Head,
            WatchedFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        });
        h.Run.PrCommentLedger = PrCommentLedgerJson.Serialize(delivered.Ledger);

        await h.Build().CloseAsync("wi-1", "11", resolve: false, h.Run.Id);

        // Same finding, restated under a fresh id against the new head.
        var restated = new RemotePrReviewItem("review", "99", "PRRT_99", "r2", "src/A.cs", 10, Finding, "Copilot",
            moved, new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc), false, false);

        var raised = Assert.Single(PrCommentDelivery.Decide(
            new RemotePrReviewLedger(
                new[] { new RemotePrReviewSummary("r2", "COMMENTED", "body", moved, DateTime.UtcNow, "Copilot", false) },
                new[] { restated }, moved, null),
            moved,
            PrCommentLedgerJson.TryParse(h.Run.PrCommentLedger)).Items);
        Assert.Equal("99", raised.CommentId);
    }
}
