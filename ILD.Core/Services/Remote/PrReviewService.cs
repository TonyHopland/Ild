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
/// The operator's half of the same queue: what a round intends to write on its
/// pull request, and the power to drop any of it before the PR node sends it.
/// Deliberately not on <see cref="IPrReviewService"/> — that is the surface an
/// agent reaches, and an agent has no business dropping the queue.
/// </summary>
public interface IPrWriteQueue
{
    Task<IReadOnlyList<PrQueuedWrite>> QueuedAsync(Guid runId);
    Task<bool> DropQueuedAsync(Guid runId, string writeId);
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
///
/// Reading is the only half that happens here. An agent never writes to a pull
/// request: a reply or a resolve is RECORDED as an intent against the run and
/// the PR node drains the queue at the end of the round, where it posts its own
/// comment. That is what keeps a human between an agent and a public pull
/// request — including a chat agent, which has no loop run of its own driving it
/// and would otherwise post the moment it was asked to. Validation still happens
/// here, at queue time, so an id this pull request does not hold is refused
/// while the agent is still listening rather than silently failing later.
/// </summary>
public sealed class PrReviewService : IPrReviewService, IPrWriteQueue
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

        return await QueueAsync(target.Run,
            new PrQueuedWrite(NewIntentId(), PrQueuedWrite.Reply, commentId, body, comment.Path, comment.Line, DateTime.UtcNow),
            "Queued: this answer goes out when the PR node next runs, and can be dropped before then.");
    }

    public async Task<RemotePrWriteResult> ResolveAsync(string workItemId, string threadId, Guid? callerRunId)
    {
        var target = await ResolveAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, null, NoPullRequest);

        var fetched = await _remote.GetPullRequestReviewLedgerAsync(target.RepoUrl, target.PrNumber);
        if (!string.IsNullOrEmpty(fetched.Message))
            return new RemotePrWriteResult(false, null, fetched.Message);

        var thread = fetched.Items.FirstOrDefault(i => string.Equals(i.ThreadId, threadId, StringComparison.Ordinal));
        if (thread is null)
            return new RemotePrWriteResult(false, null,
                $"No review thread with id '{threadId}' on this work item's pull request. The review ledger lists the thread ids that can be resolved.");

        // Only once the thread is known to exist: a provider that can never
        // resolve says so now rather than answering "queued" for something the
        // PR node could only fail at later, when nobody is listening.
        if (!await _remote.SupportsThreadResolutionAsync(target.RepoUrl))
            return new RemotePrWriteResult(false, null,
                "Resolving review threads is not supported for this repository's provider. Reply to the thread instead.");

        return await QueueAsync(target.Run,
            new PrQueuedWrite(NewIntentId(), PrQueuedWrite.Resolve, threadId, null, thread.Path, thread.Line, DateTime.UtcNow),
            "Queued: this thread is closed when the PR node next runs, and can be dropped before then.");
    }

    /// <summary>
    /// Everything a run has said it intends to write, oldest first — what the
    /// work item's PR view shows a person while the round is still running.
    /// </summary>
    public async Task<IReadOnlyList<PrQueuedWrite>> QueuedAsync(Guid runId)
        => PrCommentQueueJson.TryParse((await _runs.GetByIdAsync(runId))?.PrCommentQueue);

    /// <summary>
    /// Drop one intent before it goes out. Nothing is lost by dropping: the
    /// thread stays open and the finding stays undelivered, so a later review
    /// raises it again rather than it vanishing.
    /// </summary>
    public async Task<bool> DropQueuedAsync(Guid runId, string writeId)
    {
        var run = await _runs.GetByIdAsync(runId);
        if (run is null) return false;

        var queued = PrCommentQueueJson.TryParse(run.PrCommentQueue);
        var kept = queued.Where(q => !string.Equals(q.Id, writeId, StringComparison.Ordinal)).ToList();
        if (kept.Count == queued.Count) return false;

        await WriteQueueAsync(run, kept);
        return true;
    }

    private static string NewIntentId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Append an intent to the run's queue. The run row is re-read immediately
    /// before the write so two tools queuing in the same moment are unlikely to
    /// lose one; a lost intent is not silent damage — the finding it answered
    /// stays undelivered and comes back on the next review.
    /// </summary>
    private async Task<RemotePrWriteResult> QueueAsync(LoopRun run, PrQueuedWrite intent, string message)
    {
        var fresh = await _runs.GetByIdAsync(run.Id) ?? run;
        var queued = PrCommentQueueJson.TryParse(fresh.PrCommentQueue).ToList();
        if (queued.Count >= PrQueuedWrite.MaxQueued)
            return new RemotePrWriteResult(false, null,
                $"This run already has {queued.Count} pull-request writes waiting for the PR node; nothing more is queued until they go out.");

        queued.Add(intent);
        await WriteQueueAsync(run, queued);
        return new RemotePrWriteResult(true, intent.Id, message);
    }

    private async Task WriteQueueAsync(LoopRun run, IReadOnlyList<PrQueuedWrite> queued)
    {
        var json = queued.Count == 0 ? null : PrCommentQueueJson.Serialize(queued);
        run.PrCommentQueue = json;
        await _runs.SetPrCommentQueueAsync(run.Id, json);
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
