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
    string? Summary
);

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
