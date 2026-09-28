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
/// Read, reply and resolve — plus seeing and taking back what the agent itself
/// queued — is the whole surface: approving, merging, closing and dismissing are
/// deliberately absent. Reading happens immediately; a reply or a resolution is
/// only ever QUEUED here — the PR node writes it at the end of the round, which
/// is what keeps a human between an agent and a public pull request. Until then
/// an agent can correct its own writes, never anyone else's.
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
    [Description("Read everything said on a work item's pull request review. Use when a PR node hands you review comments, when a reason text says it is not the whole review, or before answering a reviewer — the rendered review on the forge omits findings this returns. Gives {headSha, message, reviews[], items[]}: each review carries its verdict, head commit and incomplete=true when the reviewer could not finish it (there is no judgement in those items to act on); each item carries kind (review=inline, issue=on the pull request itself, body=a review's own prose, suppressed=carried by a review body with no thread of its own), commentId, threadId, path, line, body, author, commit and resolved. A review body arrives as its own item because most of what a person says is said there rather than on a line, and a review can be a body with no inline comments at all; its id is the review's. postedByIld=true marks what ILD itself wrote — never answer those. Pass sinceCommit to see only what was written against some other commit, i.e. what is new since the one you already handled. Works on GitHub, Azure DevOps and Forgejo; the suppressed items are a GitHub Copilot artifact and simply do not occur on the other two.")]
    public Task<string> GetPrReview(
        [Description("Work item GUID (from the Chat Context, or the reason text that pointed here).")] string workItemId,
        [Description("Optional commit sha you have already dealt with; items written against it are left out.")] string? sinceCommit = null)
        => _ild.GetRawAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review"
            + (string.IsNullOrWhiteSpace(sinceCommit) ? string.Empty : $"?sinceCommit={Uri.EscapeDataString(sinceCommit)}"));

    [McpServerTool(Name = "reply_to_pr_review_comment")]
    [Description("Answer one thing a reviewer said, where they said it. Use for a finding you are NOT changing the code for — an answer posted anywhere else leaves the thread open and the same objection comes back on every later review. An inline comment is answered on its own thread; a top-level comment or a review body has no thread on any forge, so the answer goes out as a new pull-request comment quoting what it answers. Pass items[].commentId for a comment, or items[].reviewId for a body item; a suppressed finding has no id of its own, so answer the review body that carries it. Only an id that work item's own pull request holds is accepted. Nothing is posted when you call this: the answer is QUEUED against the run and the PR node sends it at the end of the round, so a human can read it and drop it first. Answering the same item again replaces your own pending answer (a close of the item included) under the same id, so there is no need to queue a correction or ask for the earlier one to be dropped. Returns {ok, commentId, writeId, message} where commentId and writeId identify the queued write; ok=false with a message is a refusal you can read, not a crash.")]
    public Task<string> ReplyToPrReviewComment(
        [Description("Work item GUID.")] string workItemId,
        [Description("What to answer, from get_pr_review: items[].commentId for a comment, or items[].reviewId for a body item.")] string commentId,
        [Description("What to say. Keep it to the point the reviewer raised.")] string body)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/reply",
            new { commentId, body });

    [McpServerTool(Name = "resolve_pr_review_thread")]
    [Description("Mark a review thread resolved, once you have fixed or answered what it raised. Use it after replying, so the finding stops being re-delivered on every later review. The threadId comes from get_pr_review; only a thread that work item's own pull request holds is accepted. Nothing happens on the forge when you call this: the resolution is QUEUED against the run and the PR node applies it at the end of the round, where a human can still drop it. Resolving a thread you already have waiting to be resolved queues nothing more. To take a resolution back before it is sent, pass its writeId to withdraw_pr_write. Returns {ok, threadId, writeId, message}. GitHub and Azure DevOps can resolve a thread; Forgejo has no API for it and answers with a message saying so — that is an answer, not a failure.")]
    public Task<string> ResolvePrReviewThread(
        [Description("Work item GUID.")] string workItemId,
        [Description("Id of the review thread to close, from get_pr_review's items[].threadId.")] string threadId)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/resolve",
            new { threadId });

    [McpServerTool(Name = "comment_on_pr")]
    [Description("Say something general on the pull request, about the round rather than about one comment. Use it when the round has something a reviewer reading the pull request needs and no single thread is the place for it — what you changed and why, a decision you took, a question you are waiting on. Do NOT use it to answer a reviewer: an answer belongs on the thread the point was raised on (reply_to_pr_review_comment), or the thread stays open and the objection comes back on every later review. There is no automatic comment any more: if you do not call this, the round says nothing general, and that is the right outcome for a round whose answers are all on threads. Nothing is posted when you call this — the comment is QUEUED against the run and the PR node sends it at the end of the round, where a human can read it and drop it first. Each call queues another comment; to take one back before it is sent, pass its writeId to withdraw_pr_write. Returns {ok, writeId, message}.")]
    public Task<string> CommentOnPr(
        [Description("Work item GUID.")] string workItemId,
        [Description("What to say about this round. ILD stamps it, so it never comes back at the loop as a new comment.")] string body)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/comment",
            new { body });

    [McpServerTool(Name = "close_pr_review_item")]
    [Description("Record that you read a review item and are deliberately not answering it — it was already dealt with in an earlier round, it does not apply, or the code it points at is gone. Use it instead of replying with something that says nothing: a reply that adds nothing is noise on the pull request, and silence alone cannot be told apart from never having read it. Set resolve=true to close its thread as well, where the item has one and the forge supports it (GitHub and Azure DevOps do; Forgejo says so and leaves the thread open). Closing suppresses nothing that was not already suppressed: the item stops being re-delivered on this head because it was handed to you, and a reviewer restating the same point against new code still reaches you. Pass items[].commentId, or items[].reviewId for a body item. Closing an item you have already answered replaces your own pending answer, and answering it again replaces the close, so there is no need to queue a correction: whichever you said last is what is sent. Returns {ok, commentId, writeId, message}; writeId is the queued resolve, or null when nothing is queued.")]
    public Task<string> ClosePrReviewItem(
        [Description("Work item GUID.")] string workItemId,
        [Description("What you are closing, from get_pr_review: items[].commentId, or items[].reviewId for a body item.")] string commentId,
        [Description("Also close its thread on the forge, where it has one and the provider can.")] bool resolve = false)
        => _ild.PostJsonAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/close",
            new { commentId, resolve });

    [McpServerTool(Name = "list_queued_pr_writes")]
    [Description("See what you have queued for the pull request and not yet had sent: your own pending replies, comments and resolves on the work item's current pull request, and nobody else's. Use it before withdraw_pr_write, or to check what the PR node is about to send on your behalf. Reads only the queue, never the forge. Returns {message, writes[]}: each write carries id, kind (reply=on a thread, comment=a new pull-request comment, resolve=closing a thread), targetId, itemId (the review item you answered or closed; null for comment_on_pr and resolve_pr_review_thread), body, path, line and queuedAt. With no pull request the list is empty and message says so.")]
    public Task<string> ListQueuedPrWrites(
        [Description("Work item GUID.")] string workItemId)
        => _ild.GetRawAsync($"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/queue");

    [McpServerTool(Name = "withdraw_pr_write")]
    [Description("Take back a write you queued for the pull request — a reply, a comment or a resolve — before the PR node sends it, when you no longer want to say anything at all. To change what you said about a review item, answer or close it again instead: that replaces your pending answer. Only your own pending writes can be withdrawn: one already sent, one another run or chat session queued, or one that does not exist is refused with a message saying why, and a human can still drop anything from the run's queue. Returns {ok, writeId, message}; ok=false with a message is a refusal you can read, not a crash.")]
    public Task<string> WithdrawPrWrite(
        [Description("Work item GUID.")] string workItemId,
        [Description("The id the queuing tool returned for the write, or an id from list_queued_pr_writes.")] string writeId)
        => _ild.DeleteAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/pr-review/queue/{Uri.EscapeDataString(writeId)}");
}
