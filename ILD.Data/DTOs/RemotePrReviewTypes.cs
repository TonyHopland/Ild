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
}

/// <summary>
/// What one run has already been handed, or written itself, on its pull request's
/// review. This is what stands between a review landing and an unattended round
/// starting: <see cref="PostedIds"/> are the comments ILD posted (the marker's
/// second, un-editable half), <see cref="DeliveredIds"/> the items a run has been
/// given, and <see cref="DeliveredHashes"/> the same findings recognised by
/// content, so a second review restating them under fresh ids starts nothing.
/// Ids are namespaced by kind because inline and pull-request-level comments live
/// in two id spaces that can collide.
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
    DateTime? WatchedFrom = null
)
{
    /// <summary>Kept per list, newest first, so a long-lived run cannot grow this column without bound.</summary>
    public const int MaxRemembered = 500;

    public static PrCommentLedger Empty { get; } = new(
        null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>The ledger key for one item, namespaced by the id space it came from.</summary>
    public static string KeyFor(string kind, string commentId) => $"{kind}:{commentId}";

    public PrCommentLedger WithPosted(string key)
        => this with { PostedIds = Remember(PostedIds, key) };

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
        DateTime? WatchedFrom);

    private const int CurrentVersion = 1;

    public static string Serialize(PrCommentLedger ledger)
        => JsonSerializer.Serialize(
            new Wire(CurrentVersion, ledger.Head, ledger.PostedIds, ledger.DeliveredIds, ledger.DeliveredHashes,
                ledger.WatchedFrom),
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
                wire.WatchedFrom);
        }
        catch (JsonException) { return null; }
    }
}
