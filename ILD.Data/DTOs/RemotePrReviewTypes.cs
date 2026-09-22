using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ILD.Data.DTOs;

/// <summary>
/// One submitted review on a pull request. <see cref="Body"/> is kept because
/// most of a Copilot review never becomes a thread — the findings it suppressed
/// live only in this prose. <see cref="Incomplete"/> marks a review the reviewer
/// could not finish (this repository's reviewer is killed by its own spend guard
/// roughly every other run): there is no judgement in it to act on.
/// </summary>
public record RemotePrReviewSummary(
    string Id,
    string? State,
    string? Body,
    string? HeadSha,
    DateTime SubmittedAt,
    string Author,
    bool Incomplete
);

/// <summary>
/// One thing a reviewer said, wherever it was said. <see cref="Kind"/> is
/// <c>review</c> (an inline diff comment), <c>issue</c> (a comment on the pull
/// request itself) or <c>suppressed</c> (a finding a review body carries without
/// surfacing it as a thread, which therefore has no comment id of its own).
/// <see cref="Commit"/> is the commit the comment was written against, not the
/// head it has since drifted onto — the per-head repeat suppression keys on it.
/// </summary>
public record RemotePrReviewItem(
    string Kind,
    string? CommentId,
    string? ThreadId,
    string? ReviewId,
    string? Path,
    int? Line,
    string Body,
    string Author,
    string? Commit,
    DateTime CreatedAt,
    bool Resolved,
    bool PostedByIld
);

/// <summary>
/// One review thread as the forge models it: the provider's own thread handle,
/// whether it is resolved, and the comments that belong to it. Only providers
/// with a thread concept (GitHub, over GraphQL) report these; the rest leave
/// each comment keyed by the root of its reply chain.
/// </summary>
public record RemotePrReviewThread(
    string ThreadId,
    IReadOnlyList<string> CommentIds,
    bool Resolved
);

/// <summary>
/// Everything said on a pull request's review, as the <c>get_pr_review</c> agent
/// tool and the PR heartbeat both read it. Deliberately a ledger rather than the
/// rendered review: on PR #158 the first review carried eight findings that never
/// appeared as threads and were invisible to every later overview.
/// <see cref="Message"/> is non-null when the forge could not be read, which is
/// an answer rather than an error — the same degradation
/// <see cref="RemoteCiLog.Unavailable"/> uses.
/// </summary>
public record RemotePrReviewLedger(
    IReadOnlyList<RemotePrReviewSummary> Reviews,
    IReadOnlyList<RemotePrReviewItem> Items,
    string? HeadSha,
    string? Message
)
{
    public static RemotePrReviewLedger Unavailable(string message)
        => new(Array.Empty<RemotePrReviewSummary>(), Array.Empty<RemotePrReviewItem>(), null, message);
}

/// <summary>
/// The outcome of writing to a pull request. <see cref="Ok"/> keeps the meaning
/// the old <c>bool</c> had — the caller fails only on a refused write — while
/// <see cref="Id"/> is best-effort: a post the provider accepted but whose id
/// could not be read is a success that records nothing, not a failure.
/// </summary>
public record RemotePrWriteResult(bool Ok, string? Id, string? Message);

/// <summary>
/// The hidden stamp every comment ILD posts carries, and the way to recognise
/// one coming back. Invisible on the pull request (an HTML comment), and never
/// an author check: ILD writes through the repository's own forge credentials,
/// so its comments and a human's arrive under one login and filtering by author
/// would swallow the human's.
/// </summary>
public static class PrCommentMarker
{
    private const string Prefix = "<!-- ild:pr-reply run=";

    public static string Stamp(string body, Guid runId)
        => $"{body.TrimEnd()}\n\n{Prefix}{runId:N} -->";

    public static bool IsStamped(string? body)
        => body is not null && body.Contains(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether ILD wrote this item — the one place that question is answered,
    /// because it cannot be answered from <see cref="RemotePrReviewItem.Body"/>
    /// alone. An item's body is capped where it is read, and the marker sits at
    /// the end, so on the PR node's own answer — a rendered
    /// <c>{{PreviousNode.Output}}</c>, routinely far longer than the cap — the
    /// stored body has had the marker cut off it.
    /// <see cref="RemotePrReviewItem.PostedByIld"/> is decided at the boundary
    /// from the full text and is therefore the authority; the body is still
    /// checked as well, for an item built by hand rather than read from a forge.
    /// </summary>
    public static bool WasPostedByIld(RemotePrReviewItem item)
        => item.PostedByIld || IsStamped(item.Body);
}

/// <summary>
/// Something a round intends to write on the pull request, recorded rather than
/// done. Agents never post: they queue, and the PR node drains the queue at the
/// end of the round, where it posts its own comment. That is what puts a human
/// between an agent and a public pull request — the queue is visible from the
/// moment it is written until the round reaches the node, and anything in it can
/// be dropped. <see cref="Path"/> and <see cref="Line"/> are carried only so the
/// person reading the queue can see where an answer would land without opening
/// the transcript.
///
/// <see cref="SourceHash"/> is the content fingerprint of the finding this
/// answers, kept so that dropping the answer — or a forge refusing it — can put
/// that finding back within reach. Null on a queue written before this existed,
/// and on an intent with no single finding behind it.
/// </summary>
public record PrQueuedWrite(
    string Id,
    string Kind,
    string TargetId,
    string? Body,
    string? Path,
    int? Line,
    DateTime QueuedAt,
    string? SourceHash = null
)
{
    public const string Reply = "reply";
    public const string Resolve = "resolve";

    /// <summary>
    /// A new pull-request comment answering something with no thread of its own
    /// — a top-level comment, or a review body. Quoting what it answers is the
    /// only thread a forge offers there.
    /// </summary>
    public const string Comment = "comment";

    /// <summary>Cap on one run's queue, so a looping agent cannot grow the column without bound.</summary>
    public const int MaxQueued = 100;
}

/// <summary>
/// The wire form of a run's queue of intended pull-request writes, persisted on
/// <c>LoopRun.PrCommentQueue</c>. Degrades to an empty queue on a blob it cannot
/// read — a queue that cannot be parsed must not be posted.
/// </summary>
public static class PrCommentQueueJson
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    public static string Serialize(IReadOnlyList<PrQueuedWrite> queued)
        => JsonSerializer.Serialize(queued, Options);

    public static IReadOnlyList<PrQueuedWrite> TryParse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<PrQueuedWrite>();
        try
        {
            return JsonSerializer.Deserialize<List<PrQueuedWrite>>(json, Options)
                ?? (IReadOnlyList<PrQueuedWrite>)Array.Empty<PrQueuedWrite>();
        }
        catch (JsonException) { return Array.Empty<PrQueuedWrite>(); }
    }
}

/// <summary>
/// What one run has already been handed, or written itself, on its pull request's
/// review. This is what stands between a review landing and an unattended round
/// starting: <see cref="PostedIds"/> are the comments ILD posted (the marker's
/// second, un-editable half), <see cref="DeliveredIds"/> the items a run has been
/// given, and <see cref="DeliveredHashes"/> the same findings recognised by
/// content, so a second review restating them under fresh ids starts nothing.
/// Ids are namespaced by kind because inline and pull-request-level comments live
/// in two id spaces that can collide, and each provider's adapter must hand back
/// an id from the space its own ledger keys on or none at all.
/// Hashes are scoped to <see cref="Head"/> and dropped when it moves — the same
/// finding restated against new code is a new finding; ids live for the run.
/// </summary>
/// <param name="WatchedFrom">
/// Set only when this ledger was opened by a <em>write</em> — the PR node
/// recording the comment it just posted — rather than by a watch. Such a ledger
/// has never looked at the pull request, so without this it would claim to have
/// seen nothing, and the first tick after the edge is wired would hand the agent
/// the PR's whole history, ILD's own pre-marker replies included. It means
/// "everything already on the pull request at this instant is history"; the
/// first delivery pass records that history and clears it.
/// </param>
public record PrCommentLedger(
    string? Head,
    IReadOnlyList<string> PostedIds,
    IReadOnlyList<string> DeliveredIds,
    IReadOnlyList<string> DeliveredHashes,
    DateTime? WatchedFrom = null,
    IReadOnlyList<string>? Unanswered = null,
    IReadOnlyList<string>? Handed = null
)
{
    /// <summary>Kept per list, newest first, so a long-lived run cannot grow this column without bound.</summary>
    public const int MaxRemembered = 500;

    public static PrCommentLedger Empty { get; } = new(
        null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// Fingerprints handed to the round that nothing has answered yet. What the
    /// PR node asks before posting its general comment: a round that answered
    /// every one of them has said its piece on the threads, and the general
    /// comment on top is a second notification carrying nothing. A round that
    /// answered some — or only resolved threads, which says nothing to anyone —
    /// still has something to report.
    /// </summary>
    public IReadOnlyList<string> Outstanding => Unanswered ?? Array.Empty<string>();

    /// <summary>
    /// Everything this round was handed, answered or not. The other half of the
    /// same question, and not derivable from <see cref="Outstanding"/>: a round
    /// that was handed three findings and answered all three leaves that list
    /// empty, and so does a round nobody said anything to. The first has already
    /// spoken on the threads; the second would lose its only statement.
    /// </summary>
    public IReadOnlyList<string> HandedThisRound => Handed ?? Array.Empty<string>();

    public PrCommentLedger WithUnanswered(IEnumerable<string> hashes)
        => this with { Unanswered = hashes.Distinct(StringComparer.Ordinal).Take(MaxRemembered).ToList() };

    /// <summary>These findings were handed to the round, and none of them is answered yet.</summary>
    public PrCommentLedger HandedOver(IEnumerable<string> hashes)
    {
        var added = hashes.ToArray();
        return WithUnanswered(Outstanding.Concat(added)) with
        {
            Handed = HandedThisRound.Concat(added).Distinct(StringComparer.Ordinal).Take(MaxRemembered).ToList(),
        };
    }

    /// <summary>An answer was queued for this finding, so it is no longer waiting.</summary>
    public PrCommentLedger Answered(string? hash)
        => string.IsNullOrEmpty(hash)
            ? this
            : WithUnanswered(Outstanding.Where(h => !string.Equals(h, hash, StringComparison.Ordinal)));

    /// <summary>
    /// Close the round's account: both lists ask about the round that just
    /// ended, not about the run. Carried forward, the first finding a round
    /// fixes in code without replying on its thread makes every later round
    /// look like it still owes a report — which is the second notification this
    /// whole mechanism exists to stop — and a stale "nothing waiting" lets a
    /// round that was handed nothing swallow the one comment it had to post.
    /// </summary>
    public PrCommentLedger RoundOver() => this with { Unanswered = null, Handed = null };

    /// <summary>The ledger key for one item, namespaced by the id space it came from.</summary>
    public static string KeyFor(string kind, string commentId) => $"{kind}:{commentId}";

    public PrCommentLedger WithPosted(string key)
        => this with { PostedIds = Remember(PostedIds, key) };

    /// <summary>
    /// Put a finding back within reach: forget that this CONTENT was delivered,
    /// while still remembering the comment that carried it.
    ///
    /// Only the fingerprint, deliberately. Forgetting the id too would let the
    /// same comment fire again on the very next tick, and a forge that keeps
    /// refusing — or a person who keeps dropping — would get an answer, a
    /// refusal and another firing every minute for ever. Forgetting only the
    /// content is what the promise actually needs: the comment already handed
    /// over stays handed over, and a LATER review restating the same finding
    /// under a fresh id is no longer mistaken for something already answered.
    /// </summary>
    public PrCommentLedger ForgetDeliveredContent(string? hash)
        => string.IsNullOrEmpty(hash)
            ? this
            : this with
            {
                DeliveredHashes = DeliveredHashes.Where(h => !string.Equals(h, hash, StringComparison.Ordinal)).ToList(),
            };

    /// <summary>The list with <paramref name="added"/> in front, duplicates dropped and the oldest beyond the cap forgotten.</summary>
    public static IReadOnlyList<string> Remember(IReadOnlyList<string> existing, params string[] added)
    {
        var kept = new List<string>(added.Length + existing.Count);
        kept.AddRange(added);
        foreach (var value in existing)
            if (!kept.Contains(value, StringComparer.Ordinal))
                kept.Add(value);
        return kept.Count <= MaxRemembered ? kept : kept[..MaxRemembered];
    }

    /// <summary>
    /// What makes two comments the same finding said twice: the place and the
    /// prose, with whitespace normalized away — a re-run reviewer re-indents and
    /// re-wraps what it said before. Hashed rather than stored verbatim so the
    /// column stays a fixed size whatever the review's prose costs.
    /// </summary>
    public static string Fingerprint(string? path, int? line, string body)
    {
        var normalized = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{line}|{normalized}"));
        return Convert.ToHexString(bytes)[..32];
    }
}

/// <summary>
/// The wire form of a <see cref="PrCommentLedger"/> persisted on
/// <c>LoopRun.PrCommentLedger</c>. One owner for both directions, as
/// <see cref="PrSnapshotJson"/> is for the snapshot, and a parse that degrades to
/// null on a corrupt blob rather than failing the heartbeat that read it.
/// </summary>
public static class PrCommentLedgerJson
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    private sealed record Wire(
        [property: JsonPropertyName("v")] int Version,
        string? Head,
        IReadOnlyList<string>? PostedIds,
        IReadOnlyList<string>? DeliveredIds,
        IReadOnlyList<string>? DeliveredHashes,
        DateTime? WatchedFrom,
        IReadOnlyList<string>? Unanswered,
        IReadOnlyList<string>? Handed);

    private const int CurrentVersion = 1;

    public static string Serialize(PrCommentLedger ledger)
        => JsonSerializer.Serialize(
            new Wire(CurrentVersion, ledger.Head, ledger.PostedIds, ledger.DeliveredIds, ledger.DeliveredHashes,
                ledger.WatchedFrom, ledger.Unanswered, ledger.Handed),
            Options);

    public static PrCommentLedger? TryParse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(json, Options);
            if (wire is null || wire.Version != CurrentVersion) return null;
            // A blob written before WatchedFrom existed simply has none, which
            // reads as a ledger that has watched — what those all were.
            return new PrCommentLedger(
                wire.Head,
                wire.PostedIds ?? Array.Empty<string>(),
                wire.DeliveredIds ?? Array.Empty<string>(),
                wire.DeliveredHashes ?? Array.Empty<string>(),
                wire.WatchedFrom,
                wire.Unanswered,
                wire.Handed);
        }
        catch (JsonException) { return null; }
    }
}
