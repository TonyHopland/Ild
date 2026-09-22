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
            .Where(item => !PrCommentMarker.WasPostedByIld(item))
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

        // A ledger opened by a write rather than a watch has never looked at the
        // pull request, so everything already on it at that moment is history,
        // not something to hand an agent — otherwise wiring the edge after a
        // round has posted would deliver the PR's whole past in one firing.
        // The history is recorded as delivered, because once WatchedFrom is
        // cleared only the ids and fingerprints stand between it and a firing.
        var recorded = new List<RemotePrReviewItem>();
        var deliver = new List<RemotePrReviewItem>();
        foreach (var item in candidates)
        {
            // Both sides to UTC first: the stamp is UtcNow, while a forge that
            // times its comments with an offset rather than a Z parses to Local,
            // and comparing the two raw would skew the window by the host's
            // offset — long enough to record a real comment as history.
            if (state.WatchedFrom is { } from && item.CreatedAt.ToUniversalTime() <= from.ToUniversalTime())
            {
                recorded.Add(item);
                continue;
            }
            if (KeyOf(item) is { } key && suppressedIds.Contains(key))
                continue;
            var fingerprint = PrCommentLedger.Fingerprint(item.Path, item.Line, item.Body);
            if (!suppressedHashes.Add(fingerprint))
                continue;
            deliver.Add(item);
            recorded.Add(item);
        }

        return new PrCommentDecision(deliver, Record(state, head, recorded) with { WatchedFrom = null });
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

    /// <summary>
    /// Fold a set of handed-over items into a ledger. Public because a writer
    /// that lost a compare-and-set has to re-apply the SAME handover to whatever
    /// the ledger says now, rather than writing back a value computed from the
    /// copy it started with.
    /// </summary>
    public static PrCommentLedger Record(PrCommentLedger state, string? head, IReadOnlyList<RemotePrReviewItem> delivered)
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
    /// What identifies an item across ticks. A comment has the forge's own id.
    /// A suppressed finding has none — it exists only inside a review body — so
    /// it is keyed by the review that carried it and the place it points at,
    /// which is just as stable. Leaving it to the content fingerprint alone
    /// would mean no key at all that outlives a head change, and since the
    /// fingerprints are dropped on every head change, the loop's own push would
    /// re-deliver the same old review's findings every single round.
    /// </summary>
    private static string? KeyOf(RemotePrReviewItem item)
    {
        if (item.CommentId is not null)
            return PrCommentLedger.KeyFor(item.Kind, item.CommentId);
        return item.ReviewId is not null && item.Path is not null
            ? PrCommentLedger.KeyFor(item.Kind, $"{item.ReviewId}:{item.Path}:{item.Line}")
            : null;
    }
}
