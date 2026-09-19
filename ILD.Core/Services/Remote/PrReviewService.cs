using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Reads and answers a work item's pull request review on behalf of an agent.
/// Scoped (owns a DbContext via the stores).
///
/// <c>callerRunId</c> is the run the call came from (the <c>X-ILD-Run-Id</c>
/// header), which is not always the work item's own: a chat agent borrows these
/// same tools. It decides whether a read consumes what it returned, and names
/// the run in the marker a reply carries.
/// </summary>
public interface IPrReviewService
{
    Task<RemotePrReviewLedger> ReadAsync(string workItemId, string? sinceCommit, Guid? callerRunId);
    Task<RemotePrWriteResult> ReplyAsync(string workItemId, string commentId, string body, Guid? callerRunId);
    Task<RemotePrWriteResult> ResolveAsync(string workItemId, string threadId, Guid? callerRunId);
}

/// <summary>
/// The agent-facing half of a review, and the reason an answer stops coming
/// back: a reason text can only carry so much of a batched review, so this is
/// where an agent reads the rest — every inline comment, every finding the
/// review body suppressed, each thread's state — and where it answers on the
/// thread the objection was raised on instead of into a general comment that
/// leaves the thread open.
///
/// The forge credentials live here, not with the agent, exactly as they do for
/// <c>get_ci_log</c>. A comment or thread id is only honoured when the work
/// item's own current pull request freshly holds it, which is what stops these
/// tools being pointed at an arbitrary pull request elsewhere in the forge.
/// Reading, replying and resolving is the whole surface: approving, merging,
/// closing and dismissing are decisions it must not be able to take.
/// </summary>
public sealed class PrReviewService : IPrReviewService
{
    private readonly ILoopRunStore _runs;
    private readonly IRemoteProvider _remote;

    public PrReviewService(ILoopRunStore runs, IRemoteProvider remote)
    {
        _runs = runs;
        _remote = remote;
    }

    private const string NoPullRequest =
        "This work item's current run has no pull request, so there is no review to read.";

    public async Task<RemotePrReviewLedger> ReadAsync(string workItemId, string? sinceCommit, Guid? callerRunId)
    {
        var target = await ResolveAsync(workItemId);
        if (target is null)
            return RemotePrReviewLedger.Unavailable(NoPullRequest);

        var fetched = await _remote.GetPullRequestReviewLedgerAsync(target.RepoUrl, target.PrNumber);
        if (!string.IsNullOrEmpty(fetched.Message))
            return fetched;

        var posted = PrCommentLedgerJson.TryParse(target.Run.PrCommentLedger)?.PostedIds ?? Array.Empty<string>();
        var flagged = fetched.Items.Select(item => item with { PostedByIld = WrittenByIld(item, posted) }).ToList();

        // "New since C" is read as: written against some commit other than C.
        var returned = string.IsNullOrWhiteSpace(sinceCommit)
            ? flagged
            : flagged.Where(i => !string.Equals(i.Commit, sinceCommit, StringComparison.Ordinal)).ToList();

        // An item handed to the round that is going to act on it is consumed, so
        // it does not also start a round of its own — which matters most under a
        // changes-requested review, where on_rejected outranks on_comment every
        // tick and the comments are reachable only through this tool. Scoped to
        // that run, and only while it is not parked at its PR node: a chat agent
        // borrows the same tools, and an unscoped rule would let "what did the
        // review say?" silently cancel the firing those items were about to cause.
        if (callerRunId == target.Run.Id && !IsParkedAtPrNode(target.Run))
        {
            var decision = PrCommentDelivery.Decide(
                fetched with { Items = returned },
                fetched.HeadSha,
                PrCommentLedgerJson.TryParse(target.Run.PrCommentLedger));
            await _runs.SetPrCommentLedgerAsync(target.Run.Id, PrCommentLedgerJson.Serialize(decision.Ledger));
        }

        return fetched with { Items = returned };
    }

    public async Task<RemotePrWriteResult> ReplyAsync(string workItemId, string commentId, string body, Guid? callerRunId)
    {
        var target = await ResolveAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, null, NoPullRequest);

        var fetched = await _remote.GetPullRequestReviewLedgerAsync(target.RepoUrl, target.PrNumber);
        if (!string.IsNullOrEmpty(fetched.Message))
            return new RemotePrWriteResult(false, null, fetched.Message);

        // Only an inline review comment has a thread to answer on. A
        // pull-request-level comment or a suppressed finding would be refused by
        // the forge anyway; saying which it is here is the useful answer.
        var comment = fetched.Items.FirstOrDefault(i => string.Equals(i.CommentId, commentId, StringComparison.Ordinal));
        if (comment is null)
            return new RemotePrWriteResult(false, null,
                $"No comment with id '{commentId}' on this work item's pull request. The review ledger lists the comment ids that can be answered.");
        if (comment.Kind != "review")
            return new RemotePrWriteResult(false, null,
                $"Comment '{commentId}' is not an inline review comment ({comment.Kind}), so it has no thread to reply on. Answer an item whose kind is 'review'.");

        var result = await _remote.ReplyToReviewThreadAsync(
            target.RepoUrl, target.PrNumber, commentId,
            PrCommentMarker.Stamp(body, callerRunId ?? target.Run.Id));

        if (result is { Ok: true, Id: not null })
            await RecordPostedAsync(target.Run, fetched, PrCommentLedger.KeyFor("review", result.Id));

        return result;
    }

    public async Task<RemotePrWriteResult> ResolveAsync(string workItemId, string threadId, Guid? callerRunId)
    {
        var target = await ResolveAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, null, NoPullRequest);

        var fetched = await _remote.GetPullRequestReviewLedgerAsync(target.RepoUrl, target.PrNumber);
        if (!string.IsNullOrEmpty(fetched.Message))
            return new RemotePrWriteResult(false, null, fetched.Message);

        if (!fetched.Items.Any(i => string.Equals(i.ThreadId, threadId, StringComparison.Ordinal)))
            return new RemotePrWriteResult(false, null,
                $"No review thread with id '{threadId}' on this work item's pull request. The review ledger lists the thread ids that can be resolved.");

        return await _remote.ResolveReviewThreadAsync(target.RepoUrl, target.PrNumber, threadId);
    }

    private sealed record Target(LoopRun Run, string RepoUrl, string PrNumber);

    private async Task<Target?> ResolveAsync(string workItemId)
    {
        var run = await _runs.GetCurrentByWorkItemAsync(workItemId);
        if (run?.PrUrl is null)
            return null;

        var repoUrl = RemotePrUrl.ExtractRepoUrl(run.PrUrl);
        var prNumber = RemotePrUrl.ExtractPrNumber(run.PrUrl);
        return repoUrl is null || prNumber is null ? null : new Target(run, repoUrl, prNumber);
    }

    private static bool IsParkedAtPrNode(LoopRun run)
        => run.Status == LoopRunStatus.WaitingHuman
            && string.Equals(run.HumanFeedbackReason, HumanFeedbackReasons.PrAwaitingMerge, StringComparison.Ordinal);

    private static bool WrittenByIld(RemotePrReviewItem item, IReadOnlyList<string> postedIds)
        => PrCommentMarker.WasPostedByIld(item)
            || (item.CommentId is not null
                && postedIds.Contains(PrCommentLedger.KeyFor(item.Kind, item.CommentId), StringComparer.Ordinal));

    /// <summary>
    /// Record a comment ILD just wrote, so the heartbeat never hands it back as
    /// something to answer. A run with no ledger yet is seeded from what is
    /// already on the pull request first: leaving it empty would claim the run
    /// had seen nothing, and the next tick would deliver the PR's whole history.
    /// </summary>
    private async Task RecordPostedAsync(LoopRun run, RemotePrReviewLedger fetched, string key)
    {
        var state = PrCommentLedgerJson.TryParse(run.PrCommentLedger)
            ?? PrCommentDelivery.Decide(fetched, fetched.HeadSha, null).Ledger;
        var json = PrCommentLedgerJson.Serialize(state.WithPosted(key));
        run.PrCommentLedger = json;
        await _runs.SetPrCommentLedgerAsync(run.Id, json);
    }
}
