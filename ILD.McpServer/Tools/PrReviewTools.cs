using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tools for a pull request's review: read the whole of it, answer one
/// comment on its own thread, and close that thread. The rendered review hides
/// most of what a reviewer found — on one real pull request eight findings lived
/// only inside the review body — so reading the ledger is the only way to see
/// all of it, and answering on the thread is the only way an answer stops coming
/// back. Everything runs server-side with the forge credentials, which never
/// reach the agent.
///
/// Read, reply and resolve is the whole surface: approving, merging, closing and
/// dismissing are deliberately absent.
///
/// Drift warning: this shape must stay in lockstep with the agent-API endpoints
/// (<c>AgentController</c>) it calls.
/// </summary>
[McpServerToolType]
public sealed class PrReviewTools
{
    private readonly IldClient _ild;

    public PrReviewTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_pr_review")]
    [Description("Read everything said on a work item's pull request review. Use when a PR node hands you review comments, when a reason text says it is not the whole review, or before answering a reviewer — the rendered review on the forge omits findings this returns. Gives {headSha, message, reviews[], items[]}: each review carries its verdict, head commit and incomplete=true when the reviewer could not finish it (there is no judgement in those items to act on); each item carries kind (review=inline, issue=on the pull request itself, suppressed=carried by a review body with no thread of its own), commentId, threadId, path, line, body, author, commit and resolved. postedByIld=true marks what ILD itself wrote — never answer those. Pass sinceCommit to see only what was written against some other commit, i.e. what is new since the one you already handled. Works on GitHub, Azure DevOps and Forgejo; the suppressed items are a GitHub Copilot artifact and simply do not occur on the other two.")]
    public Task<string> GetPrReview(
        [Description("Work item GUID (from the Chat Context, or the reason text that pointed here).")] string workItemId,
        [Description("Optional commit sha you have already dealt with; items written against it are left out.")] string? sinceCommit = null)
        => _ild.GetRawAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review"
            + (string.IsNullOrWhiteSpace(sinceCommit) ? string.Empty : $"?sinceCommit={Uri.EscapeDataString(sinceCommit)}"));

    [McpServerTool(Name = "reply_to_pr_review_comment")]
    [Description("Answer one review comment where it was made, on its own thread. Use for a finding you are NOT changing the code for — an answer posted anywhere else leaves the thread open and the same objection comes back on every later review. The commentId comes from get_pr_review; only an id that work item's own pull request holds is accepted. Returns {ok, commentId, message}: ok=false with a message is a refusal you can read, not a crash.")]
    public Task<string> ReplyToPrReviewComment(
        [Description("Work item GUID.")] string workItemId,
        [Description("Id of the review comment to answer, from get_pr_review's items[].commentId.")] string commentId,
        [Description("What to say. Keep it to the point the reviewer raised.")] string body)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/reply",
            new { commentId, body });

    [McpServerTool(Name = "resolve_pr_review_thread")]
    [Description("Mark a review thread resolved, once you have fixed or answered what it raised. Use it after replying, so the finding stops being re-delivered on every later review. The threadId comes from get_pr_review; only a thread that work item's own pull request holds is accepted. Returns {ok, threadId, message}. GitHub and Azure DevOps can resolve a thread; Forgejo has no API for it and answers with a message saying so — that is an answer, not a failure.")]
    public Task<string> ResolvePrReviewThread(
        [Description("Work item GUID.")] string workItemId,
        [Description("Id of the review thread to close, from get_pr_review's items[].threadId.")] string threadId)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/resolve",
            new { threadId });
}
