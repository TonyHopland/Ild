using System.Text.Json;
using System.Text.Json.Serialization;

namespace ILD.Data.DTOs;

public record RemotePrResult(
    string? Url,
    string? HtmlUrl,
    RemotePrStatus Status,
    string? Error
);

public enum RemotePrStatus
{
    Open,
    Closed,
    Merged
}

public record RemotePrComment(
    string Id,
    string Body,
    string Author,
    DateTime CreatedAt
);

/// <summary>
/// Aggregate continuous-integration verdict for a PR's head commit, derived
/// from check runs and commit statuses combined. Serialized by its string name
/// (not its ordinal) so the persisted snapshot's wire shape matches the
/// frontend's <c>"None" | "Pending" | "Passed" | "Failed"</c> union.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemotePrCiStatus
{
    /// <summary>No check runs or commit statuses reported.</summary>
    None,
    /// <summary>Checks exist but at least one is still running (and none failed).</summary>
    Pending,
    /// <summary>Every reported check completed successfully.</summary>
    Passed,
    /// <summary>At least one check failed / timed out / was cancelled, or a status is failure/error.</summary>
    Failed
}

/// <summary>
/// One failing check on a PR's head commit — a check run, or a legacy commit
/// status flattened into the same shape (its context, state, target URL and
/// description). Recorded so a red PR can say *what* broke: the aggregate
/// <see cref="RemotePrCiStatus"/> only says *that* something did, which leaves
/// a loop's fix-it agent guessing.
/// <see cref="Summary"/> is the provider's own check output (summary + text, or
/// a status description), truncated where it is captured — CI output has no
/// upper bound and this is persisted on every heartbeat tick.
/// </summary>
public record RemotePrCheck(
    string Name,
    string Conclusion,
    string? Url,
    string? Summary,
    string? CheckId
);

/// <summary>
/// A window onto one failing check's log, as the <c>get_ci_log</c> agent tool
/// returns it. The whole log is never inlined anywhere: an agent that finds
/// <see cref="RemotePrCheck.Summary"/> insufficient asks for a tail, and pages
/// backwards with <see cref="Offset"/> from what it learns here.
/// <see cref="Available"/> is false — with <see cref="Message"/> saying why and
/// where a human can look instead — when the provider has no fetchable log,
/// which is an answer rather than an error.
/// </summary>
public record RemoteCiLog(
    bool Available,
    string? Text,
    int Lines,
    int Offset,
    int TotalLines,
    bool Truncated,
    string? Message
)
{
    public static RemoteCiLog Unavailable(string message)
        => new(false, null, 0, 0, 0, false, message);
}

/// <summary>
/// One entry in a PR's conversation. <see cref="Kind"/> is
/// <c>comment</c> (issue comment), <c>review_comment</c> (inline diff comment),
/// or <c>review</c> (a submitted review, whose verdict is in <see cref="State"/>).
/// </summary>
public record RemotePrConversationEntry(
    string Kind,
    string Author,
    string Body,
    DateTime CreatedAt,
    string? State
);

/// <summary>
/// Full point-in-time view of a pull request, fetched by the PR heartbeat
/// poller. Carries the display fields the feedback UI renders plus the state
/// the engine routes on (mergeability, CI verdict, review decision). Persisted
/// per <c>LoopRun</c> and diffed tick-over-tick to detect state transitions.
/// <see cref="FailedChecks"/> is empty unless <see cref="Ci"/> is
/// <see cref="RemotePrCiStatus.Failed"/>, and deserializes as <c>null</c> on a
/// snapshot persisted before the field existed — read it defensively.
/// </summary>
public record RemotePrSnapshot(
    string? Title,
    string? Body,
    string State,
    bool Merged,
    bool? Mergeable,
    string? MergeableState,
    RemotePrCiStatus Ci,
    IReadOnlyList<RemotePrCheck> FailedChecks,
    bool Approved,
    bool ChangesRequested,
    IReadOnlyList<RemotePrConversationEntry> Conversation,
    DateTime FetchedAt
);

/// <summary>
/// The wire form of a <see cref="RemotePrSnapshot"/> persisted on
/// <c>LoopRun.PrSnapshot</c>. One owner for both directions: the heartbeat
/// poller writes through <see cref="Serialize"/>, and the taskboard projection,
/// the webhook resume path and the PR node read back through
/// <see cref="TryParse"/>. Camel-cased because the API hands the stored blob to
/// the frontend verbatim, which is also why the writer's options and the
/// readers' cannot be allowed to drift apart in separate copies.
/// </summary>
public static class PrSnapshotJson
{
    private static readonly JsonSerializerOptions Options = JsonSerializerOptions.Web;

    public static string Serialize(RemotePrSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, Options);

    /// <summary>
    /// The snapshot stored in <paramref name="json"/>, or null when there is
    /// none yet or the blob is unreadable — a corrupt snapshot degrades the
    /// caller (a badge, a reason string) rather than failing it.
    /// </summary>
    public static RemotePrSnapshot? TryParse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<RemotePrSnapshot>(json, Options); }
        catch (JsonException) { return null; }
    }
}
