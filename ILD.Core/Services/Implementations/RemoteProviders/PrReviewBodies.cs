using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.RemoteProviders;

/// <summary>
/// A review's own prose, delivered as an item like any other.
///
/// Most of what a person says on a pull request is said in the review body, not
/// on a line: "Some improvements to be made" submitted with one inline comment,
/// or with none at all. Until this existed the loop was handed the inline
/// comments and nothing else, so a body-only review fired no edge and the agent
/// could not read the body even through the tool — the work item asks for the
/// review body *plus* its comments.
///
/// Applied once here, where all three providers pass through, rather than in
/// each adapter: the three build their <see cref="RemotePrReviewLedger.Reviews"/>
/// differently but they all carry the body, the id and the commit already.
/// </summary>
public static class PrReviewBodies
{
    /// <summary>The item kind a review body arrives as; keyed <c>body:&lt;reviewId&gt;</c>.</summary>
    public const string Kind = "body";

    /// <summary>
    /// The same budget every other item gets (RemoteGitProviderAdapterBase.
    /// MaxReviewItemLength). A Copilot overview runs to thousands of characters
    /// and the signal carrying a batch is capped at 8192, so one unshortened
    /// body would crowd the rest of the review out of it.
    /// </summary>
    private const int MaxBodyLength = 1000;

    /// <summary>
    /// The ledger with one extra item per review that has something to say.
    ///
    /// A review the reviewer could not finish contributes nothing, exactly as
    /// its inline comments do — the verdict is not there to act on yet. Every
    /// other throttle applies downstream without knowing this happened: the
    /// marker on the body still says whether ILD wrote it, and the content
    /// fingerprint still recognises the same words submitted twice.
    /// </summary>
    public static RemotePrReviewLedger Include(RemotePrReviewLedger ledger)
    {
        if (!string.IsNullOrEmpty(ledger.Message))
            return ledger;

        var bodies = ledger.Reviews
            .Where(r => !r.Incomplete)
            .Where(r => !string.IsNullOrWhiteSpace(r.Body))
            .Select(r => new RemotePrReviewItem(
                Kind,
                // No comment id: a body is not a comment, and there is nothing
                // to answer it on. The review id is what identifies it.
                CommentId: null,
                ThreadId: null,
                ReviewId: r.Id,
                Path: null,
                Line: null,
                Shorten(r.Body!),
                r.Author,
                r.HeadSha,
                r.SubmittedAt,
                Resolved: false,
                PrCommentMarker.IsStamped(r.Body!)))
            .ToList();

        if (bodies.Count == 0)
            return ledger;

        // Bodies first: a review's verdict reads before the lines it is about.
        return ledger with { Items = bodies.Concat(ledger.Items).ToList() };
    }

    private static string Shorten(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= MaxBodyLength ? trimmed : trimmed[..MaxBodyLength] + "…";
    }
}
