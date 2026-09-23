using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// A finding the review body suppressed has no comment id — it exists only
/// inside that body — so what identifies it across ticks decides whether it can
/// ever stop being delivered. Keyed by content alone it could not: content
/// fingerprints are scoped to the head commit and dropped whenever it moves, and
/// the loop's own push moves it every round, so the same old review's findings
/// would be handed over again after every single round. That is the money pump
/// the feature exists to prevent, reached without ILD posting anything at all.
/// </summary>
public class PrCommentSuppressedIdentityTests
{
    private const string HeadA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HeadB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HeadC = "cccccccccccccccccccccccccccccccccccccccc";

    private static RemotePrReviewItem Suppressed(
        string path, int line, string body, string reviewId = "r1", string commit = HeadA)
        => new("suppressed", null, null, reviewId, path, line, body, "Copilot", commit,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Fetched(string head, params RemotePrReviewItem[] items)
        => new(
            new[] { new RemotePrReviewSummary("r1", "COMMENTED", "body", HeadA,
                new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Utc), "Copilot", false) },
            items, head, null);

    /// <summary>PR #158's first review: eight findings that never became threads.</summary>
    private static RemotePrReviewItem[] TheReviewsHiddenFindings() =>
        Enumerable.Range(1, 8)
            .Select(i => Suppressed($"src/File{i}.cs", i * 10, $"a finding about File{i}"))
            .ToArray();

    [Fact]
    public void The_same_reviews_hidden_findings_are_not_handed_over_again_after_a_push()
    {
        var watching = PrCommentDelivery.Decide(Fetched(HeadA), HeadA, null).Ledger;

        var first = PrCommentDelivery.Decide(Fetched(HeadA, TheReviewsHiddenFindings()), HeadA, watching);
        Assert.Equal(8, first.Items.Count);

        // The round fixes them and pushes: the head moves, but the review that
        // carried them is still sitting on the pull request, still saying so.
        var afterPush = PrCommentDelivery.Decide(Fetched(HeadB, TheReviewsHiddenFindings()), HeadB, first.Ledger);
        Assert.Empty(afterPush.Items);

        // …and again, because a loop pushes every round.
        var afterAnotherPush = PrCommentDelivery.Decide(
            Fetched(HeadC, TheReviewsHiddenFindings()), HeadC, afterPush.Ledger);
        Assert.Empty(afterAnotherPush.Items);
    }

    [Fact]
    public void A_later_review_that_finds_the_same_thing_again_still_fires()
    {
        // A fresh review restating it after the code moved is a new judgement on
        // new code, not the old body being re-read.
        var watching = PrCommentDelivery.Decide(Fetched(HeadA), HeadA, null).Ledger;
        var first = PrCommentDelivery.Decide(
            Fetched(HeadA, Suppressed("src/A.cs", 10, "still allocating per row")), HeadA, watching);
        Assert.Single(first.Items);

        var second = PrCommentDelivery.Decide(
            Fetched(HeadB, Suppressed("src/A.cs", 10, "still allocating per row", reviewId: "r2", commit: HeadB)),
            HeadB, first.Ledger);

        Assert.Single(second.Items);
    }

    [Fact]
    public void Two_findings_from_one_review_on_the_same_file_are_told_apart_by_their_line()
    {
        var watching = PrCommentDelivery.Decide(Fetched(HeadA), HeadA, null).Ledger;

        var decision = PrCommentDelivery.Decide(
            Fetched(HeadA,
                Suppressed("src/A.cs", 10, "the first thing"),
                Suppressed("src/A.cs", 92, "the second thing")),
            HeadA, watching);

        Assert.Equal(2, decision.Items.Count);
        Assert.Empty(PrCommentDelivery.Decide(
            Fetched(HeadB,
                Suppressed("src/A.cs", 10, "the first thing"),
                Suppressed("src/A.cs", 92, "the second thing")),
            HeadB, decision.Ledger).Items);
    }

    [Fact]
    public void A_cut_short_reviews_hidden_findings_are_not_recorded_and_still_fire_later()
    {
        var watching = PrCommentDelivery.Decide(Fetched(HeadA), HeadA, null).Ledger;
        var truncated = new RemotePrReviewLedger(
            new[] { new RemotePrReviewSummary("r9", "COMMENTED", "body", HeadA, DateTime.UtcNow, "Copilot", true) },
            new[] { Suppressed("src/A.cs", 10, "half a thought", reviewId: "r9") },
            HeadA, null);

        var nothing = PrCommentDelivery.Decide(truncated, HeadA, watching);
        Assert.Empty(nothing.Items);

        var complete = PrCommentDelivery.Decide(
            Fetched(HeadA, Suppressed("src/A.cs", 10, "half a thought", reviewId: "r10")), HeadA, nothing.Ledger);
        Assert.Single(complete.Items);
    }
}
