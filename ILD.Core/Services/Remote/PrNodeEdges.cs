using System.Text;
using ILD.Data;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Remote;

/// <summary>
/// The state each of the eight reserved PR outputs fires on (the names and their
/// priority are defined once, in <see cref="LoopOutputs.ReservedPr"/>), and the
/// pick of a single one when several states newly become true in one heartbeat
/// tick. The PR heartbeat poller emits a <c>NodeSignal.Custom</c> for the
/// highest-priority output that is both newly-true and actually wired;
/// everything else only updates the persisted snapshot. Also owns the prose each
/// output resumes the node with (<see cref="Describe"/>) — the same vocabulary,
/// said in words for the agent downstream. See the PR Node entry in CONTEXT.md.
/// </summary>
public static class PrNodeEdges
{
    /// <summary>
    /// The set of edge-state names that are currently true for a snapshot. A
    /// closed PR surfaces only its terminal state (<c>on_merged</c> /
    /// <c>on_abandoned</c>); an open PR surfaces the review/conflict/CI states. <c>on_comment</c>
    /// is never among them: it is decided from the run's own delivery ledger (see
    /// <c>PrCommentDelivery</c>), which is what keeps a comment ILD itself posted
    /// from starting a round.
    /// </summary>
    public static HashSet<string> ActiveStates(RemotePrSnapshot s)
    {
        var states = new HashSet<string>(StringComparer.Ordinal);
        if (string.Equals(s.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            states.Add(s.Merged ? LoopOutputs.OnMerged : LoopOutputs.OnAbandoned);
            return states;
        }

        if (s.ChangesRequested) states.Add(LoopOutputs.OnRejected);
        if (s.Mergeable == false || string.Equals(s.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase))
            states.Add(LoopOutputs.OnMergeConflict);
        if (s.Ci == RemotePrCiStatus.Failed) states.Add(LoopOutputs.OnCiFailed);
        if (s.Approved) states.Add(LoopOutputs.OnApproved);
        if (s.Ci == RemotePrCiStatus.Passed) states.Add(LoopOutputs.OnCiPassed);
        return states;
    }

    /// <summary>The highest-priority edge name in <paramref name="candidates"/>, or null when empty.</summary>
    public static string? HighestPriority(IEnumerable<string> candidates)
    {
        var set = candidates as ISet<string> ?? new HashSet<string>(candidates, StringComparer.Ordinal);
        return LoopOutputs.ReservedPr.FirstOrDefault(set.Contains);
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
    ///
    /// The review edges get the same treatment through
    /// <c>get_pr_review</c>, and that pointer is budgeted rather than merely
    /// appended: a batched <c>on_comment</c> reason is the case that overflows,
    /// and truncation takes the tail, so an appended pointer would be the first
    /// thing a long batch lost — leaving a half-list and no way to read the rest.
    /// </summary>
    public static string Describe(
        string? edge, RemotePrSnapshot? snapshot = null, string? detail = null, string? workItemId = null)
    {
        var body = string.IsNullOrWhiteSpace(detail) ? DetailFor(edge, snapshot, workItemId) : detail.Trim();
        var text = body.Length == 0 ? Headline(edge) : $"{Headline(edge)}\n\n{body}";
        // A pointer that would leave no room for the reason it points past is
        // not worth the reason: drop it rather than truncate into the marker.
        var pointer = ReviewPointer(edge, workItemId);
        if (pointer.Length > MaxReasonLength / 2) pointer = string.Empty;

        var budget = MaxReasonLength - pointer.Length;
        return (text.Length <= budget ? text : Truncate(text, budget)) + pointer;
    }

    private const string TruncationMarker = "\n… (truncated)";

    /// <summary>
    /// Cut to <paramref name="max"/> characters counting the marker, so the cap
    /// is a length a caller can rely on, and never between the halves of a
    /// surrogate pair — CI output and review bodies carry emoji.
    /// </summary>
    private static string Truncate(string text, int max)
    {
        var cut = max - TruncationMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + TruncationMarker;
    }

    /// <summary>
    /// The way out of a reason that can only hold so much of a review. Offered
    /// on the two review edges only, and only when there is a work item id to
    /// spell into the call — an agent that has to guess it will not make it.
    /// <c>on_rejected</c> gets it too because while changes are requested it
    /// outranks <c>on_comment</c> every tick, so that round would otherwise see
    /// the review's prose and never the comments behind it.
    /// </summary>
    private static string ReviewPointer(string? edge, string? workItemId)
        => edge is LoopOutputs.OnComment or LoopOutputs.OnRejected && !string.IsNullOrWhiteSpace(workItemId)
            ? "\n\nThis is not the whole review. To read every comment on it — inline and suppressed, with its thread id and whether that thread is resolved — call "
              + $"get_pr_review(workItemId: \"{workItemId}\")."
            : string.Empty;

    private static string Headline(string? edge) => edge switch
    {
        LoopOutputs.OnRejected => "A reviewer requested changes on the pull request.",
        LoopOutputs.OnMergeConflict => "The pull request conflicts with its target branch and cannot be merged.",
        LoopOutputs.OnCiFailed => "CI failed on the pull request.",
        LoopOutputs.OnComment => "New comments arrived on the pull request.",
        LoopOutputs.OnApproved => "The pull request was approved.",
        LoopOutputs.OnCiPassed => "CI passed on the pull request.",
        LoopOutputs.OnMerged => "The pull request was merged.",
        LoopOutputs.OnAbandoned => "The pull request was closed without being merged.",
        _ => "The pull request changed state.",
    };

    private static string DetailFor(string? edge, RemotePrSnapshot? snapshot, string? workItemId)
    {
        if (snapshot is null) return string.Empty;

        return edge switch
        {
            LoopOutputs.OnCiFailed => DescribeFailedChecks(snapshot, workItemId),
            LoopOutputs.OnRejected => LatestChangesRequestedReview(snapshot),
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
