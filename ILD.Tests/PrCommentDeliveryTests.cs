using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// Every throttle that stands between a review landing and a full verification
/// round starting, decided in one pure place so each rule can be stated on its
/// own: what ILD itself wrote, what a run has already been handed, what is the
/// same finding said twice, and what came out of a review that was cut short.
/// </summary>
public class PrCommentDeliveryTests
{
    private const string HeadA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HeadB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static DateTime _clock = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime Next() => _clock = _clock.AddMinutes(1);

    private static RemotePrReviewItem Inline(
        string id,
        string path = "src/A.cs",
        int line = 10,
        string body = "this allocation is wrong",
        string author = "Copilot",
        string reviewId = "r1",
        string? threadId = null,
        string commit = HeadA,
        bool resolved = false)
        => new("review", id, threadId ?? id, reviewId, path, line, body, author, commit, Next(), resolved, false);

    private static RemotePrReviewItem Issue(
        string id,
        string body = "please rename the flag",
        string author = "tony",
        string commit = HeadA)
        => new("issue", id, null, null, null, null, body, author, commit, Next(), false, false);

    private static RemotePrReviewItem Suppressed(
        string path,
        int line,
        string body,
        string reviewId = "r1",
        string commit = HeadA)
        => new("suppressed", null, null, reviewId, path, line, body, "Copilot", commit, Next(), false, false);

    private static RemotePrReviewSummary Review(string id = "r1", bool incomplete = false, string head = HeadA)
        => new(id, "COMMENTED", "review body", head, Next(), "Copilot", incomplete);

    private static RemotePrReviewLedger Fetched(string head, IReadOnlyList<RemotePrReviewSummary> reviews, params RemotePrReviewItem[] items)
        => new(reviews, items, head, null);

    private static RemotePrReviewLedger Fetched(string head, params RemotePrReviewItem[] items)
        => Fetched(head, new[] { Review(head: head) }, items);

    /// <summary>
    /// The ledger a run carries once the first watch has recorded what was
    /// already on the PR — the state every later tick starts from.
    /// </summary>
    private static PrCommentLedger Watching(string head, params RemotePrReviewItem[] alreadyThere)
        => PrCommentDelivery.Decide(Fetched(head, alreadyThere), head, null).Ledger;

    [Fact]
    public void First_watch_records_everything_already_on_the_pull_request_and_fires_nothing()
    {
        // Left emergent, a run upgraded mid-flight would be handed the whole
        // history of its own PR — including its own pre-marker replies.
        var fetched = Fetched(HeadA, Inline("1"), Inline("2", path: "src/B.cs"), Issue("3"));

        var first = PrCommentDelivery.Decide(fetched, HeadA, null);

        Assert.Empty(first.Items);

        var second = PrCommentDelivery.Decide(fetched, HeadA, first.Ledger);
        Assert.Empty(second.Items);
    }

    [Fact]
    public void Only_what_appeared_after_the_first_watch_fires()
    {
        var state = Watching(HeadA, Inline("1"), Issue("2"));

        var decision = PrCommentDelivery.Decide(
            Fetched(HeadA, Inline("1"), Issue("2"), Inline("3", path: "src/C.cs", body: "new finding")),
            HeadA, state);

        var item = Assert.Single(decision.Items);
        Assert.Equal("3", item.CommentId);
    }

    [Fact]
    public void Every_undelivered_item_found_in_one_tick_comes_back_as_a_single_batch()
    {
        // One round per review, never one per comment: a review carrying four
        // inline comments and two suppressed ones is one firing of six items.
        var state = Watching(HeadA);
        var fetched = Fetched(HeadA,
            new[] { Review("r2") },
            Inline("11", reviewId: "r2"),
            Inline("12", path: "src/B.cs", reviewId: "r2"),
            Inline("13", path: "src/C.cs", reviewId: "r2"),
            Issue("14"),
            Suppressed("src/D.cs", 4, "a hidden finding", reviewId: "r2"),
            Suppressed("src/E.cs", 9, "another hidden finding", reviewId: "r2"));

        var decision = PrCommentDelivery.Decide(fetched, HeadA, state);

        Assert.Equal(6, decision.Items.Count);
    }

    [Fact]
    public void A_comment_carrying_ilds_own_marker_never_fires()
    {
        var state = Watching(HeadA);
        var reply = PrCommentMarker.Stamp("Fixed in the latest commit.", Guid.NewGuid());

        var decision = PrCommentDelivery.Decide(Fetched(HeadA, Issue("99", body: reply)), HeadA, state);

        Assert.Empty(decision.Items);
    }

    [Fact]
    public void A_human_comment_with_byte_identical_visible_text_but_no_marker_does_fire()
    {
        const string text = "Fixed in the latest commit.";
        var state = Watching(HeadA);
        var stamped = PrCommentMarker.Stamp(text, Guid.NewGuid());
        Assert.Contains(text, stamped, StringComparison.Ordinal);

        var decision = PrCommentDelivery.Decide(
            Fetched(HeadA, Issue("99", body: stamped), Issue("100", body: text)),
            HeadA, state);

        var item = Assert.Single(decision.Items);
        Assert.Equal("100", item.CommentId);
    }

    [Fact]
    public void The_marker_is_an_html_comment_so_it_never_shows_on_the_pull_request()
    {
        var stamped = PrCommentMarker.Stamp("Answered inline.", Guid.NewGuid());

        Assert.Contains("<!--", stamped, StringComparison.Ordinal);
        Assert.Contains("-->", stamped, StringComparison.Ordinal);
        Assert.True(PrCommentMarker.IsStamped(stamped));
        Assert.False(PrCommentMarker.IsStamped("Answered inline."));
        Assert.False(PrCommentMarker.IsStamped(null));
    }

    [Fact]
    public void An_item_from_the_account_ild_posts_under_still_fires_when_it_is_not_ilds()
    {
        // ILD writes through the repository's own forge credentials, so its
        // comments and a human's arrive under one login. Filtering by author
        // would swallow the human — worse than the bug it would fix.
        const string sharedAccount = "ild-service-account";
        var state = Watching(HeadA);

        var decision = PrCommentDelivery.Decide(
            Fetched(HeadA,
                Issue("200", body: PrCommentMarker.Stamp("ILD's own answer", Guid.NewGuid()), author: sharedAccount),
                Issue("201", body: "and one a person typed", author: sharedAccount)),
            HeadA, state);

        var item = Assert.Single(decision.Items);
        Assert.Equal("201", item.CommentId);
    }

    [Fact]
    public void An_item_already_handed_to_the_run_does_not_fire_again()
    {
        var state = Watching(HeadA);
        var fetched = Fetched(HeadA, Inline("11"), Issue("12"));

        var first = PrCommentDelivery.Decide(fetched, HeadA, state);
        Assert.Equal(2, first.Items.Count);

        var second = PrCommentDelivery.Decide(fetched, HeadA, first.Ledger);
        Assert.Empty(second.Items);
    }

    [Fact]
    public void A_second_review_restating_earlier_findings_on_an_unchanged_head_fires_nothing()
    {
        // Same file, line and prose under fresh comment ids: the reviewer ran
        // again, not a new objection. By id alone this would start a round.
        var state = Watching(HeadA);
        var first = PrCommentDelivery.Decide(
            Fetched(HeadA, new[] { Review("r1") },
                Inline("11", path: "src/A.cs", line: 10, body: "new byte[file.Length] will not compile", reviewId: "r1"),
                Suppressed("src/B.cs", 20, "prefer the returned file name", reviewId: "r1")),
            HeadA, state);
        Assert.Equal(2, first.Items.Count);

        var second = PrCommentDelivery.Decide(
            Fetched(HeadA, new[] { Review("r2") },
                Inline("21", path: "src/A.cs", line: 10, body: "  new byte[file.Length] will not compile  ", reviewId: "r2"),
                Suppressed("src/B.cs", 20, "prefer the returned file name", reviewId: "r2")),
            HeadA, first.Ledger);

        Assert.Empty(second.Items);
    }

    [Fact]
    public void The_same_finding_restated_against_new_code_fires_once_the_head_has_moved()
    {
        var state = Watching(HeadA);
        var first = PrCommentDelivery.Decide(
            Fetched(HeadA, Inline("11", path: "src/A.cs", line: 10, body: "still allocating per row")),
            HeadA, state);
        Assert.Single(first.Items);

        var second = PrCommentDelivery.Decide(
            Fetched(HeadB, new[] { Review("r2", head: HeadB) },
                Inline("21", path: "src/A.cs", line: 10, body: "still allocating per row", reviewId: "r2", commit: HeadB)),
            HeadB, first.Ledger);

        var item = Assert.Single(second.Items);
        Assert.Equal("21", item.CommentId);
    }

    [Fact]
    public void An_item_already_delivered_stays_delivered_across_a_head_change()
    {
        // Fingerprints are scoped to a head; ids are not. The same comment id
        // re-read after a push is still the comment the run already answered.
        var state = Watching(HeadA);
        var first = PrCommentDelivery.Decide(Fetched(HeadA, Inline("11")), HeadA, state);
        Assert.Single(first.Items);

        var second = PrCommentDelivery.Decide(
            Fetched(HeadB, new[] { Review("r1", head: HeadB) }, Inline("11", commit: HeadA)),
            HeadB, first.Ledger);

        Assert.Empty(second.Items);
    }

    [Fact]
    public void A_new_comment_on_a_thread_ild_resolved_still_fires()
    {
        // GitHub does not un-resolve a thread when someone replies to it, and
        // ILD resolves what it answers — so "resolved" would silently swallow
        // the human's follow-up.
        var state = Watching(HeadA, Inline("11", threadId: "t1"));

        var decision = PrCommentDelivery.Decide(
            Fetched(HeadA,
                Inline("11", threadId: "t1", resolved: true),
                Inline("12", threadId: "t1", body: "this is still broken, see below", author: "tony", resolved: true)),
            HeadA, state);

        var item = Assert.Single(decision.Items);
        Assert.Equal("12", item.CommentId);
    }

    [Fact]
    public void A_review_that_was_cut_short_contributes_nothing_to_a_firing()
    {
        // This repository's reviewer is killed by its own spend guard roughly
        // every other review; there is no judgement in what it managed to emit.
        var state = Watching(HeadA);

        var decision = PrCommentDelivery.Decide(
            Fetched(HeadA, new[] { Review("r9", incomplete: true) },
                Inline("31", reviewId: "r9"),
                Suppressed("src/B.cs", 7, "half a thought", reviewId: "r9")),
            HeadA, state);

        Assert.Empty(decision.Items);
    }

    [Fact]
    public void A_cut_short_reviews_findings_still_fire_when_a_complete_review_repeats_them()
    {
        var state = Watching(HeadA);
        var truncated = PrCommentDelivery.Decide(
            Fetched(HeadA, new[] { Review("r9", incomplete: true) },
                Inline("31", path: "src/A.cs", line: 10, body: "guard the null case", reviewId: "r9")),
            HeadA, state);
        Assert.Empty(truncated.Items);

        var complete = PrCommentDelivery.Decide(
            Fetched(HeadA, new[] { Review("r9", incomplete: true), Review("r10") },
                Inline("31", path: "src/A.cs", line: 10, body: "guard the null case", reviewId: "r9"),
                Inline("41", path: "src/A.cs", line: 10, body: "guard the null case", reviewId: "r10")),
            HeadA, truncated.Ledger);

        var item = Assert.Single(complete.Items);
        Assert.Equal("41", item.CommentId);
    }

    [Fact]
    public void A_complete_reviews_items_fire_in_a_tick_that_also_carries_a_cut_short_one()
    {
        var state = Watching(HeadA);

        var decision = PrCommentDelivery.Decide(
            Fetched(HeadA, new[] { Review("r9", incomplete: true), Review("r10") },
                Inline("31", path: "src/X.cs", body: "half a thought", reviewId: "r9"),
                Inline("41", path: "src/Y.cs", body: "a whole one", reviewId: "r10")),
            HeadA, state);

        var item = Assert.Single(decision.Items);
        Assert.Equal("41", item.CommentId);
    }

    [Fact]
    public void An_unreadable_ledger_from_the_forge_fires_nothing_and_keeps_the_run_state()
    {
        var state = Watching(HeadA, Inline("11"));

        var decision = PrCommentDelivery.Decide(
            new RemotePrReviewLedger(Array.Empty<RemotePrReviewSummary>(), Array.Empty<RemotePrReviewItem>(), null,
                "The provider request failed."),
            HeadA, state);

        Assert.Empty(decision.Items);
        var back = PrCommentDelivery.Decide(Fetched(HeadA, Inline("11")), HeadA, decision.Ledger);
        Assert.Empty(back.Items);
    }
}
