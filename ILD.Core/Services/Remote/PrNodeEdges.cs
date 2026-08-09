using System.Text;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Remote;

/// <summary>
/// The seven reserved custom-edge names a PR node may declare, the state each
/// fires on, and the priority used to pick a single edge when several states
/// newly become true in one heartbeat tick. The PR heartbeat poller emits a
/// <c>NodeSignal.Custom</c> for the highest-priority edge that is both
/// newly-true and actually connected; everything else only updates the
/// persisted snapshot. Also owns the prose each edge resumes the node with
/// (<see cref="Describe"/>) — the same vocabulary, said in words for the agent
/// downstream. See the PR Node entry in CONTEXT.md.
/// </summary>
public static class PrNodeEdges
{
    public const string OnRejected = "on_rejected";
    public const string OnMergeConflict = "on_merge_conflict";
    public const string OnCiFailed = "on_ci_failed";
    public const string OnApproved = "on_approved";
    public const string OnCiPassed = "on_ci_passed";
    public const string OnMerged = "on_merged";
    public const string OnAbandoned = "on_abandoned";

    /// <summary>Reserved edge names in descending priority (index 0 = highest).</summary>
    public static readonly IReadOnlyList<string> ByPriority = new[]
    {
        OnRejected,
        OnMergeConflict,
        OnCiFailed,
        OnApproved,
        OnCiPassed,
        OnMerged,
        OnAbandoned,
    };

    /// <summary>
    /// The set of edge-state names that are currently true for a snapshot. A
    /// closed PR surfaces only its terminal state (<c>on_merged</c> /
    /// <c>on_abandoned</c>); an open PR surfaces the review/conflict/CI states.
    /// </summary>
    public static HashSet<string> ActiveStates(RemotePrSnapshot s)
    {
        var states = new HashSet<string>(StringComparer.Ordinal);
        if (string.Equals(s.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            states.Add(s.Merged ? OnMerged : OnAbandoned);
            return states;
        }

        if (s.ChangesRequested) states.Add(OnRejected);
        if (s.Mergeable == false || string.Equals(s.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase))
            states.Add(OnMergeConflict);
        if (s.Ci == RemotePrCiStatus.Failed) states.Add(OnCiFailed);
        if (s.Approved) states.Add(OnApproved);
        if (s.Ci == RemotePrCiStatus.Passed) states.Add(OnCiPassed);
        return states;
    }

    /// <summary>The highest-priority edge name in <paramref name="candidates"/>, or null when empty.</summary>
    public static string? HighestPriority(IEnumerable<string> candidates)
    {
        var set = candidates as ISet<string> ?? new HashSet<string>(candidates, StringComparer.Ordinal);
        return ByPriority.FirstOrDefault(set.Contains);
    }

    /// <summary>
    /// Longest reason <see cref="Describe"/> produces. The text becomes the
    /// resumed PR node's output, i.e. the next node's
    /// <c>{{PreviousNode.Output}}</c>, so it is held to the same 8192-character
    /// scale as a loop variable — CI output is otherwise unbounded.
    /// </summary>
    public const int MaxReasonLength = 8192;

    /// <summary>
    /// Why a PR node is being resumed, in prose the next node can act on: a
    /// headline for <paramref name="edge"/> plus whatever detail is available —
    /// the failing checks behind <c>on_ci_failed</c>, the review behind
    /// <c>on_rejected</c>. Both resume paths (heartbeat poller and webhook) send
    /// this as the signal's output, so an agent asked to fix red CI is told what
    /// broke instead of inferring it. <paramref name="detail"/> is a caller's own
    /// better text (a webhook's review comment) and replaces the snapshot-derived
    /// detail; an unknown or absent edge still yields a sentence rather than the
    /// empty string that made this necessary.
    ///
    /// The failing-check detail is a summary, and a summary is often not the
    /// error. Each check therefore carries the id <c>get_ci_log</c> takes, and
    /// <paramref name="workItemId"/> — which the agent has no placeholder for —
    /// is spelled into the call so the next step is a tool call it can make
    /// rather than a link it cannot follow.
    /// </summary>
    public static string Describe(
        string? edge, RemotePrSnapshot? snapshot = null, string? detail = null, string? workItemId = null)
    {
        var body = string.IsNullOrWhiteSpace(detail) ? DetailFor(edge, snapshot, workItemId) : detail.Trim();
        var text = body.Length == 0 ? Headline(edge) : $"{Headline(edge)}\n\n{body}";
        return text.Length <= MaxReasonLength ? text : Truncate(text);
    }

    private const string TruncationMarker = "\n… (truncated)";

    /// <summary>
    /// Cut to <see cref="MaxReasonLength"/> counting the marker, so the cap is
    /// the length a caller can rely on, and never between the halves of a
    /// surrogate pair — CI output carries emoji and box-drawing characters.
    /// </summary>
    private static string Truncate(string text)
    {
        var cut = MaxReasonLength - TruncationMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + TruncationMarker;
    }

    private static string Headline(string? edge) => edge switch
    {
        OnRejected => "A reviewer requested changes on the pull request.",
        OnMergeConflict => "The pull request conflicts with its target branch and cannot be merged.",
        OnCiFailed => "CI failed on the pull request.",
        OnApproved => "The pull request was approved.",
        OnCiPassed => "CI passed on the pull request.",
        OnMerged => "The pull request was merged.",
        OnAbandoned => "The pull request was closed without being merged.",
        _ => "The pull request changed state.",
    };

    private static string DetailFor(string? edge, RemotePrSnapshot? snapshot, string? workItemId)
    {
        if (snapshot is null) return string.Empty;

        return edge switch
        {
            OnCiFailed => DescribeFailedChecks(snapshot, workItemId),
            OnRejected => LatestChangesRequestedReview(snapshot),
            _ => string.Empty,
        };
    }

    private static string DescribeFailedChecks(RemotePrSnapshot snapshot, string? workItemId)
    {
        var checks = snapshot.FailedChecks;
        if (checks is null || checks.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var check in checks)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("### ").Append(check.Name).Append(" — ").Append(check.Conclusion);
            if (!string.IsNullOrWhiteSpace(check.CheckId)) sb.Append("\ncheck id: ").Append(check.CheckId);
            if (!string.IsNullOrWhiteSpace(check.Url)) sb.Append('\n').Append(check.Url);
            if (!string.IsNullOrWhiteSpace(check.Summary)) sb.Append('\n').Append(check.Summary!.Trim());
        }

        // The summary above is what the provider chose to say, not the error.
        // Name the way out of it, with the arguments filled in — an agent that
        // has to guess the work item id will not make the call.
        if (checks.Any(c => !string.IsNullOrWhiteSpace(c.CheckId)))
            sb.Append("\n\nThese summaries are not the full log. To read it, call get_ci_log(workItemId: \"")
                .Append(workItemId ?? "<this work item>")
                .Append("\", checkId: \"<check id above>\") — it returns the end of the log, and takes an offset to page further back.");

        return sb.ToString();
    }

    private static string LatestChangesRequestedReview(RemotePrSnapshot snapshot)
    {
        var review = snapshot.Conversation?
            .Where(e => e.Kind == "review"
                && string.Equals(e.State, "CHANGES_REQUESTED", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(e.Body))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault();
        return review is null ? string.Empty : $"{review.Author}:\n{review.Body.Trim()}";
    }

    /// <summary>Parse the comma-separated persisted baseline back into a set.</summary>
    public static HashSet<string> ParseStates(string? csv)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(csv)) return set;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);
        return set;
    }
}
