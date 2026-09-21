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
/// same tools. It decides whether a READ consumes what it returned. Reply and
/// resolve take it only because this surface is fixed by its callers: they
/// queue rather than write, and the marker a reply carries is stamped at drain
/// time from the run the PR node is executing.
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
    private readonly IRunNotifier? _notifier;

    /// <summary>
    /// <paramref name="notifier"/> is optional only so a test can build the
    /// service with the two collaborators it is really about; DI always supplies
    /// it. Without it a queued answer sits in the database unseen until
    /// something else happens to refresh the run, which is most of the window a
    /// person has to drop it.
    /// </summary>
    public PrReviewService(ILoopRunStore runs, IRemoteProvider remote, IRunNotifier? notifier = null)
    {
        _runs = runs;
        _remote = remote;
        _notifier = notifier;
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
    /// Drop one intent before it goes out. Nothing is lost by dropping: the
    /// thread stays open and the finding stays undelivered, so a later review
    /// raises it again rather than it vanishing.
    /// </summary>
    public Task<bool> DropQueuedAsync(Guid runId, string writeId)
        => MutateQueueAsync(runId, queued =>
        {
            var kept = queued.Where(q => !string.Equals(q.Id, writeId, StringComparison.Ordinal)).ToList();
            return kept.Count == queued.Count ? null : kept;
        });

    private static string NewIntentId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Attempts before a contended queue gives up; each one re-reads the row it lost to.</summary>
    private const int QueueWriteAttempts = 5;

    /// <summary>
    /// Read the queue, apply <paramref name="change"/>, and write it back only
    /// if nothing else moved it meanwhile — retrying on the row it lost to.
    /// Plain read-modify-write loses one of two simultaneous edits, and for a
    /// DROP that is not a lost edit but a public comment a human had stopped
    /// going out anyway. <paramref name="change"/> returns null for "nothing to
    /// do", which is the answer when the item has already gone.
    /// </summary>
    private async Task<bool> MutateQueueAsync(
        Guid runId, Func<IReadOnlyList<PrQueuedWrite>, List<PrQueuedWrite>?> change)
    {
        for (var attempt = 0; attempt < QueueWriteAttempts; attempt++)
        {
            var current = await _runs.GetPrCommentQueueAsync(runId);
            var changed = change(PrCommentQueueJson.TryParse(current));
            if (changed is null)
                return false;

            var json = changed.Count == 0 ? null : PrCommentQueueJson.Serialize(changed);
            if (await _runs.TrySetPrCommentQueueAsync(runId, current, json))
            {
                // Every change to the queue goes through here, so this is the one
                // place that has to tell the UI. The round is mid-flight when an
                // agent queues, and nothing else refreshes the run until it parks
                // — by which time the PR node has already sent it.
                if (_notifier is not null)
                    await _notifier.PrQueueChangedAsync(runId);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Append an intent to the run's queue, under the same compare-and-set as a
    /// drop so a burst of tool calls cannot lose one.
    /// </summary>
    private async Task<RemotePrWriteResult> QueueAsync(LoopRun run, PrQueuedWrite intent, string message)
    {
        var full = false;
        var written = await MutateQueueAsync(run.Id, queued =>
        {
            if (queued.Count >= PrQueuedWrite.MaxQueued)
            {
                full = true;
                return null;
            }
            full = false;
            return queued.Append(intent).ToList();
        });

        if (full)
            return new RemotePrWriteResult(false, null,
                $"This run already has {PrQueuedWrite.MaxQueued} pull-request writes waiting for the PR node; nothing more is queued until they go out.");
        if (!written)
            return new RemotePrWriteResult(false, null,
                "The queue is being changed from somewhere else; nothing was queued. Try again.");

        return new RemotePrWriteResult(true, intent.Id, message);
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

}
