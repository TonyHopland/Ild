using System.Text;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Remote;

/// <summary>What one tick hands the run, and the ledger it leaves behind.</summary>
public sealed record PrCommentDecision(IReadOnlyList<RemotePrReviewItem> Items, PrCommentLedger Ledger);

/// <summary>
/// Every throttle standing between a review landing and an unattended round
/// starting, decided in one pure place so each rule can be read on its own and
/// tested without a forge. Automated, the human is no longer the rate limiter:
/// each firing is a full verification gate, and the worst failure available here
/// is the loop answering its own answer, round after round.
///
/// What never fires: a comment ILD posted (by its hidden marker, and by the id
/// recorded when it posted — either alone is enough), an item the run has
/// already been handed, the same finding restated on an unchanged head, and
/// anything from a review the reviewer could not finish. What deliberately does
/// fire: a new comment on a thread ILD resolved — a forge does not un-resolve a
/// thread when someone replies, so resolution would swallow the follow-up — and
/// a comment from the very account ILD posts under, since ILD writes through the
/// repository's own credentials and filtering by author would swallow the human.
/// </summary>
public static class PrCommentDelivery
{
    /// <summary>
    /// The items <paramref name="state"/> has not been handed yet, all of them in
    /// one batch, together with the ledger recording that it now has.
    ///
    /// A run with no ledger yet is on its first watch: everything already on the
    /// pull request is recorded as delivered and nothing fires, because an
    /// upgrade mid-run would otherwise hand the agent the whole history of its
    /// own PR, its own pre-marker replies included.
    ///
    /// An unreadable ledger (<see cref="RemotePrReviewLedger.Message"/>) changes
    /// nothing. A caller with no state yet must not persist what comes back from
    /// one — there was nothing to record, and a ledger claiming "watching,
    /// nothing seen" would deliver the PR's whole history on the next tick.
    /// </summary>
    public static PrCommentDecision Decide(RemotePrReviewLedger fetched, string? head, PrCommentLedger? state)
    {
        if (!string.IsNullOrEmpty(fetched.Message))
            return new PrCommentDecision(Array.Empty<RemotePrReviewItem>(), state ?? PrCommentLedger.Empty);

        var incompleteReviews = fetched.Reviews
            .Where(r => r.Incomplete)
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);

        // A review that was cut short carries no judgement to act on, and its
        // items are left undelivered so a later complete review restating them
        // still fires.
        var candidates = fetched.Items
            .Where(item => !PrCommentMarker.IsStamped(item.Body))
            .Where(item => item.ReviewId is null || !incompleteReviews.Contains(item.ReviewId))
            .ToList();

        if (state is null)
            return new PrCommentDecision(Array.Empty<RemotePrReviewItem>(), Record(PrCommentLedger.Empty, head, candidates));

        var sameHead = string.Equals(state.Head, head, StringComparison.Ordinal);
        var suppressedIds = new HashSet<string>(state.DeliveredIds, StringComparer.Ordinal);
        suppressedIds.UnionWith(state.PostedIds);
        var suppressedHashes = sameHead
            ? new HashSet<string>(state.DeliveredHashes, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var deliver = new List<RemotePrReviewItem>();
        foreach (var item in candidates)
        {
            if (KeyOf(item) is { } key && suppressedIds.Contains(key))
                continue;
            var fingerprint = PrCommentLedger.Fingerprint(item.Path, item.Line, item.Body);
            if (!suppressedHashes.Add(fingerprint))
                continue;
            deliver.Add(item);
        }

        return new PrCommentDecision(deliver, Record(state, head, deliver));
    }

    /// <summary>
    /// What the batch says, one entry per item: where it is, who wrote it, the
    /// ids an agent needs to answer it, the commit the review it came from ran
    /// against, and the prose. An item with no file says so rather than arriving
    /// as a finding about nothing.
    /// </summary>
    public static string Describe(IReadOnlyList<RemotePrReviewItem> items)
    {
        var text = new StringBuilder();
        foreach (var item in items)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append("### ")
                .Append(item.Path is null ? "PR-level comment" : $"{item.Path}:{item.Line?.ToString() ?? "?"}")
                .Append(" — ")
                .Append(string.IsNullOrWhiteSpace(item.Author) ? "unknown" : item.Author);
            if (item.CommentId is not null) text.Append("\ncomment id: ").Append(item.CommentId);
            if (item.ThreadId is not null) text.Append("\nthread id: ").Append(item.ThreadId);
            if (item.Kind == "suppressed") text.Append("\n(suppressed by the review body — it has no thread to answer on)");
            if (item.Commit is not null) text.Append("\nreviewed at commit: ").Append(item.Commit);
            if (item.Resolved) text.Append("\nthread resolved: yes");
            text.Append('\n').Append(item.Body.Trim());
        }

        return text.ToString();
    }

    private static PrCommentLedger Record(PrCommentLedger state, string? head, IReadOnlyList<RemotePrReviewItem> delivered)
    {
        var sameHead = string.Equals(state.Head, head, StringComparison.Ordinal);
        return state with
        {
            Head = head,
            DeliveredIds = PrCommentLedger.Remember(
                state.DeliveredIds,
                delivered.Select(KeyOf).OfType<string>().ToArray()),
            DeliveredHashes = PrCommentLedger.Remember(
                sameHead ? state.DeliveredHashes : Array.Empty<string>(),
                delivered.Select(i => PrCommentLedger.Fingerprint(i.Path, i.Line, i.Body)).ToArray()),
        };
    }

    /// <summary>
    /// The ledger key for an item, or null for a suppressed finding, which the
    /// forge never gave an id of its own — those are recognised by content alone.
    /// </summary>
    private static string? KeyOf(RemotePrReviewItem item)
        => item.CommentId is null ? null : PrCommentLedger.KeyFor(item.Kind, item.CommentId);
}
