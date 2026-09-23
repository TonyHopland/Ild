using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// Most of what a person says on a pull request is said in the review body, not
/// on a line. Until these passed, a review whose whole content was its body
/// fired nothing and could not be read even through the tool — the loop was
/// handed the inline comments and was told nothing about the verdict they came
/// under.
/// </summary>
public class PrReviewBodyItemTests
{
    private const string Head = "9b531f43a5e7ebd52ea18f95784728765c9c9ad3";

    private static RemotePrReviewSummary Review(
        string id, string? body, bool incomplete = false, string author = "tony")
        => new(id, "COMMENTED", body, Head, new DateTime(2026, 9, 22, 13, 0, 0, DateTimeKind.Utc), author, incomplete);

    private static RemotePrReviewItem Inline(string id, string body = "this line is wrong")
        => new("review", id, $"PRRT_{id}", "r1", "README.md", 3, body, "tony", Head,
            new DateTime(2026, 9, 22, 13, 45, 0, DateTimeKind.Utc), false, false);

    private static RemotePrReviewLedger Ledger(
        IReadOnlyList<RemotePrReviewSummary> reviews, params RemotePrReviewItem[] items)
        => new(reviews, items, Head, null);

    [Fact]
    public void A_review_that_is_only_a_body_still_has_something_to_deliver()
    {
        var fetched = PrReviewBodies.Include(Ledger(new[] { Review("r1", "Some improvements to be made") }));

        var item = Assert.Single(fetched.Items);
        Assert.Equal(PrReviewBodies.Kind, item.Kind);
        Assert.Equal("Some improvements to be made", item.Body);
        Assert.Equal("r1", item.ReviewId);
        Assert.Equal(Head, item.Commit);
        Assert.Null(item.CommentId);
    }

    [Fact]
    public void A_body_only_review_fires_the_edge()
    {
        var fetched = PrReviewBodies.Include(Ledger(new[] { Review("r1", "Some improvements to be made") }));

        var decision = PrCommentDelivery.Decide(fetched, Head, PrCommentLedger.Empty with
        {
            Head = Head,
            WatchedFrom = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
        });

        var delivered = Assert.Single(decision.Items);
        Assert.Equal(PrReviewBodies.Kind, delivered.Kind);
    }

    [Fact]
    public void A_review_with_a_body_and_a_comment_delivers_both_the_body_first()
    {
        var fetched = PrReviewBodies.Include(
            Ledger(new[] { Review("r1", "Some improvements to be made") }, Inline("125")));

        var decision = PrCommentDelivery.Decide(fetched, Head, PrCommentLedger.Empty with
        {
            Head = Head,
            WatchedFrom = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
        });

        Assert.Equal(2, decision.Items.Count);
        // The verdict reads before the lines it is about.
        Assert.Equal(PrReviewBodies.Kind, decision.Items[0].Kind);
        Assert.Equal("review", decision.Items[1].Kind);
    }

    [Fact]
    public void The_same_body_is_not_delivered_twice()
    {
        var fetched = PrReviewBodies.Include(Ledger(new[] { Review("r1", "Some improvements to be made") }));
        var watching = PrCommentLedger.Empty with
        {
            Head = Head,
            WatchedFrom = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
        };

        var first = PrCommentDelivery.Decide(fetched, Head, watching);
        Assert.Single(first.Items);

        Assert.Empty(PrCommentDelivery.Decide(fetched, Head, first.Ledger).Items);
    }

    [Fact]
    public void A_body_ild_wrote_itself_never_comes_back()
    {
        var stamped = PrCommentMarker.Stamp("Answered every point.", Guid.NewGuid());
        var fetched = PrReviewBodies.Include(Ledger(new[] { Review("r1", stamped, author: "ild") }));

        Assert.True(Assert.Single(fetched.Items).PostedByIld);
        Assert.Empty(PrCommentDelivery.Decide(fetched, Head, PrCommentLedger.Empty with
        {
            Head = Head,
            WatchedFrom = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc),
        }).Items);
    }

    [Fact]
    public void A_review_the_reviewer_could_not_finish_contributes_no_body()
    {
        // Same rule its inline comments follow: there is no verdict in it yet.
        var fetched = PrReviewBodies.Include(
            Ledger(new[] { Review("r1", "Some improvements to be made", incomplete: true) }));

        Assert.Empty(fetched.Items);
    }

    [Fact]
    public void An_empty_body_is_not_an_item()
    {
        Assert.Empty(PrReviewBodies.Include(Ledger(new[] { Review("r1", null) })).Items);
        Assert.Empty(PrReviewBodies.Include(Ledger(new[] { Review("r2", "   ") })).Items);
    }

    [Fact]
    public void A_body_too_long_for_the_signal_is_shortened_like_every_other_item()
    {
        // A Copilot overview runs to thousands of characters and the signal
        // carrying the batch is capped at 8192: one unshortened body would
        // crowd out the inline findings it is the verdict on.
        var overview = new string('x', 5000);

        var item = Assert.Single(PrReviewBodies.Include(Ledger(new[] { Review("r1", overview) })).Items);

        Assert.True(item.Body.Length < overview.Length, "the body went out at full length");
        Assert.True(item.Body.Length <= 1001, $"a body of {item.Body.Length} characters still crowds the batch");
        Assert.EndsWith("…", item.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_fits_is_delivered_word_for_word()
    {
        var verdict = new string('y', 1000);

        var item = Assert.Single(PrReviewBodies.Include(Ledger(new[] { Review("r1", verdict) })).Items);

        Assert.Equal(verdict, item.Body);
    }

    [Fact]
    public void The_batch_says_where_a_body_was_said_and_how_to_answer_it()
    {
        // A body has no comment id and no thread, so without the review id
        // the agent is told the verdict and given no way to reply to it.
        var fetched = PrReviewBodies.Include(Ledger(new[] { Review("r1", "Some improvements to be made") }));

        var text = PrCommentDelivery.Describe(fetched.Items);

        Assert.Contains("review id: r1", text, StringComparison.Ordinal);
        Assert.Contains("### review body", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comment_on_the_pull_request_itself_says_so_and_answers_by_its_comment_id()
    {
        // The other item with no file. `:?` as a heading would read as a
        // finding about a line nobody can find.
        var toplevel = new RemotePrReviewItem("issue", "127", null, null, null, null,
            "Rename the project", "tony", Head, DateTime.UtcNow, false, false);

        var text = PrCommentDelivery.Describe(new[] { toplevel });

        Assert.Contains("### PR-level comment", text, StringComparison.Ordinal);
        Assert.Contains("comment id: 127", text, StringComparison.Ordinal);
        Assert.DoesNotContain("review id:", text, StringComparison.Ordinal);
    }
    [Fact]
    public void A_ledger_that_could_not_be_read_is_left_alone()
    {
        var unavailable = RemotePrReviewLedger.Unavailable("The provider request failed.");

        Assert.Same(unavailable, PrReviewBodies.Include(unavailable));
    }
}
