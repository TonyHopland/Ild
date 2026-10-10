using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Implementations.RemoteProviders;
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
/// same tools. It decides whether a READ consumes what it returned.
///
/// Together with <c>callerChatSessionId</c> (the <c>X-ILD-Chat-Session-Id</c>
/// header, used only when there is no run) it also names who owns a queued
/// write: the caller that queued it, and only that caller, may replace or
/// withdraw it. It is not what a reply is stamped with — the marker a reply
/// carries is stamped at drain time from the run the PR node is executing.
/// </summary>
public interface IPrReviewService
{
    Task<RemotePrReviewLedger> ReadAsync(string workItemId, string? sinceCommit, Guid? callerRunId);

    /// <summary>Answer an item; the caller's pending answer to the same item is replaced, not joined.</summary>
    Task<RemotePrWriteResult> ReplyAsync(
        string workItemId, string commentId, string body, Guid? callerRunId, Guid? callerChatSessionId = null);

    /// <summary>Resolve a thread; one the caller already has waiting to be resolved queues nothing more.</summary>
    Task<RemotePrWriteResult> ResolveAsync(
        string workItemId, string threadId, Guid? callerRunId, Guid? callerChatSessionId = null);

    /// <summary>
    /// The round's own account of itself, rather than an answer to something a
    /// reviewer raised. The PR node used to write this for it and could only
    /// guess when a round had anything to add; the round knows.
    /// </summary>
    Task<RemotePrWriteResult> CommentAsync(
        string workItemId, string body, Guid? callerRunId, Guid? callerChatSessionId = null);

    /// <summary>An item the round read and deliberately left alone.</summary>
    Task<RemotePrWriteResult> CloseAsync(
        string workItemId, string commentId, bool resolve, Guid? callerRunId, Guid? callerChatSessionId = null);

    /// <summary>Take back a write the caller queued, before the PR node sends it.</summary>
    Task<RemotePrWriteResult> WithdrawAsync(
        string workItemId, string writeId, Guid? callerRunId, Guid? callerChatSessionId);

    /// <summary>The writes the caller has waiting in the work item's queue.</summary>
    Task<PrQueuedWritesView> ListQueuedAsync(string workItemId, Guid? callerRunId, Guid? callerChatSessionId);
}

/// <summary>
/// The operator's half of the same queue: what a round intends to write on its
/// pull request, and the power to drop any of it before the PR node sends it.
/// Deliberately not on <see cref="IPrReviewService"/> — that is the surface an
/// agent reaches, and an agent still has no business dropping the queue. What
/// an agent may do there is correct itself: replace or withdraw a write it
/// queued, identified by its own run or chat session. That reaches nothing a
/// human, another run or another chat session wants posted, which is the
/// whole reason this half is kept apart — a write that is not the caller's
/// can only be dropped from here.
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
///
/// Until the PR node sends it, a caller can correct what it queued: answering
/// an item again replaces its pending answer under the same id, and any write
/// it queued can be withdrawn. Only its own — see <see cref="IPrWriteQueue"/>.
/// </summary>
public sealed class PrReviewService : IPrReviewService, IPrWriteQueue
{
    private readonly ILoopRunStore _runs;
    private readonly IRemoteProvider _remote;
    private readonly IRunNotifier? _notifier;
    private readonly IEventLogService? _events;

    /// <summary>
    /// <paramref name="notifier"/> and <paramref name="events"/> are optional
    /// only so a test can build the service with the two collaborators it is
    /// really about; DI always supplies both. Without the notifier a queued
    /// answer sits in the database unseen until something else happens to
    /// refresh the run, which is most of the window a person has to drop it;
    /// without the event log a closed item leaves no trace, which costs the
    /// record and not the decision.
    /// </summary>
    public PrReviewService(
        ILoopRunStore runs, IRemoteProvider remote, IRunNotifier? notifier = null, IEventLogService? events = null)
    {
        _runs = runs;
        _remote = remote;
        _notifier = notifier;
        _events = events;
    }

    private const string NoPullRequest =
        "This work item's current run has no pull request, so there is no review to read.";

    private const string NoLivePullRequest =
        "This work item has no live run with a pull request, so nothing can be written on one.";

    private const string NoQueue =
        "This work item's current run has no pull request, so nothing is waiting to be written on one.";

    public async Task<RemotePrReviewLedger> ReadAsync(string workItemId, string? sinceCommit, Guid? callerRunId)
    {
        var target = await ReadTargetAsync(workItemId);
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
            // Against the ledger as it stands, not the copy loaded before the
            // forge fetch above: the heartbeat writes this same column, and so
            // does a drop putting a finding back.
            await PrCommentLedgerWriter.MutateAsync(_runs, target.Run.Id, state =>
                PrCommentDelivery.Decide(fetched with { Items = returned }, fetched.HeadSha, state).Ledger);
        }

        return fetched with { Items = returned };
    }

    public async Task<RemotePrWriteResult> ReplyAsync(
        string workItemId, string commentId, string body, Guid? callerRunId, Guid? callerChatSessionId = null)
    {
        var target = await WriteTargetAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, null, NoLivePullRequest);

        var fetched = await _remote.GetPullRequestReviewLedgerAsync(target.RepoUrl, target.PrNumber);
        if (!string.IsNullOrEmpty(fetched.Message))
            return new RemotePrWriteResult(false, null, fetched.Message);

        var comment = Find(fetched, commentId);
        if (comment is null)
            return new RemotePrWriteResult(false, null,
                $"No comment with id '{commentId}' on this work item's pull request. The review ledger lists the comment ids that can be answered.");

        // A suppressed finding is the one thing with nowhere to go: the forge
        // never gave it an id, so there is neither a thread to reply on nor a
        // comment to quote. Answer the review body that carries it instead.
        if (comment.Kind == "suppressed")
            return new RemotePrWriteResult(false, null,
                $"Item '{commentId}' is a finding the review body carries without a comment of its own, so there is nothing to reply to. Answer the review body instead — its id is the review's.");

        // An inline comment has a thread. A top-level comment and a review body
        // do not, and most of what a person says is said in those, so the answer
        // there is a new pull-request comment that quotes what it answers.
        var write = comment.Kind == "review"
            ? new PrQueuedWrite(NewIntentId(), PrQueuedWrite.Reply, commentId, body, comment.Path, comment.Line, DateTime.UtcNow,
                PrCommentLedger.Fingerprint(comment.Path, comment.Line, comment.Body))
            : new PrQueuedWrite(NewIntentId(), PrQueuedWrite.Comment, commentId, InReplyTo(comment, body), comment.Path, comment.Line, DateTime.UtcNow,
                PrCommentLedger.Fingerprint(comment.Path, comment.Line, comment.Body));

        var placed = await PlaceAsync(target.Run, Caller.Of(callerRunId, callerChatSessionId), (comment.Kind, commentId), write);
        if (placed.Refusal is not null)
            return placed.Refusal;

        return new RemotePrWriteResult(true, placed.Id, placed.Replaced is null
            ? "Queued: this answer goes out when the PR node next runs, and can be dropped before then."
            : $"Replaced the {Named(placed.Replaced)} you had queued for this item, keeping its id: this answer goes out instead when the PR node next runs, and can be dropped before then.");
    }

    public async Task<RemotePrWriteResult> ResolveAsync(
        string workItemId, string threadId, Guid? callerRunId, Guid? callerChatSessionId = null)
    {
        var target = await WriteTargetAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, null, NoLivePullRequest);

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

        // Tied to no item: a reply and a resolve of the same thread are the
        // documented flow, not a correction, so neither replaces the other.
        var placed = await PlaceAsync(target.Run, Caller.Of(callerRunId, callerChatSessionId), null,
            new PrQueuedWrite(NewIntentId(), PrQueuedWrite.Resolve, threadId, null, thread.Path, thread.Line, DateTime.UtcNow,
                PrCommentLedger.Fingerprint(thread.Path, thread.Line, thread.Body)));
        if (placed.Refusal is not null)
            return placed.Refusal;

        return new RemotePrWriteResult(true, placed.Id, placed.AlreadyQueued
            ? "Already queued: you have this thread waiting to be closed when the PR node next runs, so nothing more was queued."
            : "Queued: this thread is closed when the PR node next runs, and can be dropped before then.");
    }

    /// <summary>
    /// The item an agent means by an id. A review body is named by its review
    /// id; everything else by its comment id, and a comment id wins outright
    /// when both match — the two come from separate counters on Forgejo, so a
    /// review id can equal a real comment id, and taking whichever was listed
    /// first would answer an inline finding with a new top-level comment
    /// instead of on its thread.
    ///
    /// One place, because every id an agent hands in goes through the same
    /// question: a reply that resolved an id differently from a close would let
    /// a round answer one item and dismiss another with the same number.
    /// </summary>
    private static RemotePrReviewItem? Find(RemotePrReviewLedger fetched, string id)
        => fetched.Items.FirstOrDefault(i => string.Equals(i.CommentId, id, StringComparison.Ordinal))
            ?? fetched.Items.FirstOrDefault(i =>
                i.Kind == PrReviewBodies.Kind && string.Equals(i.ReviewId, id, StringComparison.Ordinal));

    /// <summary>
    /// Queue a comment on the pull request itself, tied to no item.
    ///
    /// Only the round knows whether it has anything general to say, so a round
    /// with nothing to add says nothing, and the pull request carries what
    /// reviewers asked about rather than a notice per round.
    /// </summary>
    public async Task<RemotePrWriteResult> CommentAsync(
        string workItemId, string body, Guid? callerRunId, Guid? callerChatSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new RemotePrWriteResult(false, null, "A pull-request comment needs something to say.");

        var target = await WriteTargetAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, null, NoLivePullRequest);

        // No target and no source finding: it answers the round, not an item, so
        // dropping it puts nothing back — and a round may have several of these
        // to say, so none replaces another.
        var placed = await PlaceAsync(target.Run, Caller.Of(callerRunId, callerChatSessionId), null,
            new PrQueuedWrite(NewIntentId(), PrQueuedWrite.Comment, string.Empty, body, null, null, DateTime.UtcNow));
        return placed.Refusal ?? new RemotePrWriteResult(true, placed.Id,
            "Queued: this comment goes out when the PR node next runs, and can be dropped before then.");
    }

    /// <summary>
    /// Record that the round read an item and chose not to answer it, and —
    /// where the forge can and the item has a thread — close that thread.
    ///
    /// The case it exists for: a round handed two review bodies, one worth
    /// answering and one already dealt with in an earlier round. Without a way
    /// to say "considered, nothing to add", the only honest options were a reply
    /// saying nothing or silence indistinguishable from never having read it.
    ///
    /// Closing suppresses nothing. The item was already recorded as delivered
    /// when it was handed over, and that record is per-head: a reviewer
    /// restating the same point against new code still fires, which is exactly
    /// when a judgement made against old code stops being safe.
    ///
    /// A round either answers an item or closes it, so closing takes the place
    /// of the caller's own pending answer to the item, and answering again takes
    /// the place of a close. Taking an answer back here puts no finding back
    /// within reach: the item was considered, which is what closing records.
    /// </summary>
    public async Task<RemotePrWriteResult> CloseAsync(
        string workItemId, string commentId, bool resolve, Guid? callerRunId, Guid? callerChatSessionId = null)
    {
        var target = await WriteTargetAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, null, NoLivePullRequest);

        var fetched = await _remote.GetPullRequestReviewLedgerAsync(target.RepoUrl, target.PrNumber);
        if (!string.IsNullOrEmpty(fetched.Message))
            return new RemotePrWriteResult(false, null, fetched.Message);

        var item = Find(fetched, commentId);
        if (item is null)
            return new RemotePrWriteResult(false, null,
                $"No comment with id '{commentId}' on this work item's pull request. The review ledger lists the ids that can be closed.");

        PrQueuedWrite? closing = null;
        var closed = "Closed: the round read this and chose not to answer it.";
        if (resolve && item.ThreadId is not null)
        {
            if (await _remote.SupportsThreadResolutionAsync(target.RepoUrl))
            {
                closing = new PrQueuedWrite(NewIntentId(), PrQueuedWrite.Resolve, item.ThreadId, null, item.Path, item.Line,
                    DateTime.UtcNow, PrCommentLedger.Fingerprint(item.Path, item.Line, item.Body));
                closed = "Closed: the round read this and chose not to answer it, and the thread is closed when the PR node next runs.";
            }
            else
            {
                closed = "Closed, but the thread was left open: resolving review threads is not supported for this repository's provider.";
            }
        }

        // The queue first, so the record can only ever describe what happened.
        // Written before it, a queue that then refused — full, or moved under us
        // — would leave an event saying the thread was closed when nothing was
        // ever queued to close it.
        var placed = await PlaceAsync(target.Run, Caller.Of(callerRunId, callerChatSessionId), (item.Kind, commentId), closing);
        if (placed.Refusal is not null)
            return placed.Refusal;

        await RecordClosedAsync(target.Run, item, commentId);

        if (placed.Replaced is not null)
            closed += $" It replaced the {Named(placed.Replaced)} you had queued for this item.";
        else if (placed.Withdrawn is not null)
            closed += $" The {Named(placed.Withdrawn)} you had queued for this item was withdrawn.";
        return new RemotePrWriteResult(true, placed.Id, closed);
    }

    /// <summary>
    /// Take back a write the caller queued. Only the queue of the work item's
    /// current run is looked at, and only a write the caller queued itself is
    /// touched: another run's, a chat session's, or one from before owners were
    /// recorded is refused, and stays for a human to drop. Like a human drop,
    /// it puts the finding the write answered back within reach.
    /// </summary>
    public async Task<RemotePrWriteResult> WithdrawAsync(
        string workItemId, string writeId, Guid? callerRunId, Guid? callerChatSessionId)
    {
        var target = await WriteTargetAsync(workItemId);
        if (target is null)
            return new RemotePrWriteResult(false, writeId, NoLivePullRequest);

        var caller = Caller.Of(callerRunId, callerChatSessionId);
        PrQueuedWrite? withdrawn = null;
        string? refused = null;
        var written = await MutateQueueAsync(target.Run.Id, queued =>
        {
            withdrawn = queued.FirstOrDefault(q => string.Equals(q.Id, writeId, StringComparison.Ordinal));
            refused = withdrawn is null
                ? $"No write '{writeId}' is waiting in this work item's pull-request queue: it has already been sent, was dropped, or never existed. Nothing was changed."
                : !caller.Queued(withdrawn)
                    ? $"Write '{writeId}' was not queued by you — another run, a chat session or an earlier round queued it — so it is not yours to take back. A human can still drop it from the run's queue."
                    : null;
            return refused is null
                ? queued.Where(q => !string.Equals(q.Id, writeId, StringComparison.Ordinal)).ToList()
                : null;
        });

        if (refused is not null)
            return new RemotePrWriteResult(false, writeId, refused);
        if (!written)
            return new RemotePrWriteResult(false, writeId,
                "The queue is being changed from somewhere else; nothing was withdrawn. Try again.");

        await PutFindingBackAsync(target.Run.Id, withdrawn!.SourceHash);
        await RecordWithdrawnAsync(target.Run.Id, caller, withdrawn);
        return new RemotePrWriteResult(true, writeId, "Withdrawn: this write will not be sent.");
    }

    public async Task<PrQueuedWritesView> ListQueuedAsync(string workItemId, Guid? callerRunId, Guid? callerChatSessionId)
    {
        var target = await ReadTargetAsync(workItemId);
        if (target is null)
            return new PrQueuedWritesView(Array.Empty<PrQueuedWrite>(), NoQueue);

        var caller = Caller.Of(callerRunId, callerChatSessionId);
        var queued = PrCommentQueueJson.TryParse(await _runs.GetPrCommentQueueAsync(target.Run.Id));
        return new PrQueuedWritesView(queued.Where(caller.Queued).ToList(), null);
    }

    /// <summary>
    /// The only durable trace of a judgement that writes nothing. Best-effort:
    /// a store that will not take it costs the record, not the decision.
    /// </summary>
    private Task RecordClosedAsync(LoopRun run, RemotePrReviewItem item, string commentId)
    {
        var where = item.Path is null ? "the pull request" : $"{item.Path}:{item.Line?.ToString() ?? "?"}";
        return RecordAsync(run.Id, EventType.PrReviewItemClosed,
            $"Closed {commentId} on {where} without answering: {Shorten(item.Body)}");
    }

    /// <summary>
    /// What a correction changed, in full: a person may already have read the
    /// old answer under this id, and the record is where they see what it says
    /// now.
    /// </summary>
    private Task RecordReplacedAsync(Guid runId, Caller caller, PrQueuedWrite before, PrQueuedWrite after)
        => RecordAsync(runId, EventType.PrQueuedWriteReplaced,
            $"Queued write {before.Id} replaced by {caller.Name}.\nWas: {Said(before)}\nNow: {Said(after)}");

    private Task RecordWithdrawnAsync(Guid runId, Caller caller, PrQueuedWrite withdrawn)
        => RecordAsync(runId, EventType.PrQueuedWriteWithdrawn,
            $"Queued write {withdrawn.Id} withdrawn by {caller.Name}: {Said(withdrawn)}");

    /// <summary>What a write would say on the pull request; a resolve says nothing, so it is described.</summary>
    private static string Said(PrQueuedWrite write)
        => write.Kind == PrQueuedWrite.Resolve ? $"resolve thread {write.TargetId}" : write.Body ?? string.Empty;

    /// <summary>
    /// Best-effort: a store that will not take the record costs the record,
    /// not the decision it describes.
    /// </summary>
    private async Task RecordAsync(Guid runId, EventType type, string data)
    {
        if (_events is null)
            return;

        try
        {
            await _events.AppendAsync(runId, type, data);
        }
        catch { }
    }

    /// <summary>Enough of a finding to recognise it in the event log, and no more.</summary>
    private static string Shorten(string body)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
    }

    /// <summary>
    /// Drop one intent before it goes out. Nothing is lost by dropping: the
    /// thread stays open and the finding stays undelivered, so a later review
    /// raises it again rather than it vanishing.
    /// </summary>
    public async Task<bool> DropQueuedAsync(Guid runId, string writeId)
    {
        string? dropped = null;
        var changed = await MutateQueueAsync(runId, queued =>
        {
            var kept = queued.Where(q => !string.Equals(q.Id, writeId, StringComparison.Ordinal)).ToList();
            if (kept.Count == queued.Count)
                return null;
            dropped = queued.First(q => string.Equals(q.Id, writeId, StringComparison.Ordinal)).SourceHash;
            return kept;
        });

        // The poll path marked the finding delivered when it handed it over, so
        // without this a dropped answer leaves it suppressed and "dropping loses
        // nothing" is false: a later review restating it would be swallowed.
        if (changed)
            await PutFindingBackAsync(runId, dropped);
        return changed;
    }

    /// <summary>
    /// Put the finding an intent answered back within reach, because that answer
    /// is not going to arrive. Compare-and-set against the heartbeat, which
    /// writes this same column at the end of every tick and would otherwise
    /// silently undo this.
    ///
    /// Only while no write still waiting in the queue answers the same finding:
    /// that answer IS going to arrive, and nothing marks the finding delivered
    /// again when it does, so forgetting it would let a later review restating
    /// it start a round that answers it twice. The queue and the ledger are two
    /// columns with a compare-and-set each, so an answer queued between reading
    /// the queue and forgetting would slip past a plain check. Hence the fence:
    /// the queue is read again after forgetting, and if it moved, the finding
    /// is remembered again and the decision retaken against the queue as it now
    /// stands. An answer queued after the fence arrives at a finding already put
    /// back, exactly as an answer to one never handed over does.
    /// </summary>
    private async Task PutFindingBackAsync(Guid runId, string? sourceHash)
    {
        if (string.IsNullOrEmpty(sourceHash))
            return;

        for (var attempt = 0; attempt < QueueWriteAttempts; attempt++)
        {
            var queue = await _runs.GetPrCommentQueueAsync(runId);
            if (PrCommentQueueJson.TryParse(queue).Any(q => string.Equals(q.SourceHash, sourceHash, StringComparison.Ordinal)))
                return;

            string? head = null;
            var forgot = await PrCommentLedgerWriter.MutateAsync(_runs, runId, state =>
            {
                head = state?.Head;
                return state is not null && state.DeliveredHashes.Contains(sourceHash, StringComparer.Ordinal)
                    ? state.ForgetDeliveredContent(sourceHash)
                    : null;
            });
            if (!forgot || string.Equals(await _runs.GetPrCommentQueueAsync(runId), queue, StringComparison.Ordinal))
                return;

            // Remembered only on the head it was forgotten on: hashes are per-head,
            // and one carried onto a head the heartbeat has since moved to would
            // suppress the same finding restated against new code.
            await PrCommentLedgerWriter.MutateAsync(_runs, runId, state =>
                state is null || !string.Equals(state.Head, head, StringComparison.Ordinal)
                    ? null
                    : state with { DeliveredHashes = PrCommentLedger.Remember(state.DeliveredHashes, sourceHash) });
        }
    }

    /// <summary>Lines of the answered text quoted back before the answer.</summary>
    private const int QuotedLines = 8;

    /// <summary>
    /// The body of an answer to something with no thread. A forge threads only
    /// inline comments, so on a top-level comment or a review body the reply
    /// would otherwise arrive at the bottom of the conversation attached to
    /// nothing. Quoting is what a person does there, and it keeps the answer
    /// readable without following an anchor.
    /// </summary>
    private static string InReplyTo(RemotePrReviewItem item, string answer)
    {
        var what = item.Kind == PrReviewBodies.Kind
            ? $"the review{(item.ReviewId is null ? "" : $" ({item.ReviewId})")}"
            : $"the comment{(item.CommentId is null ? "" : $" ({item.CommentId})")}";
        var who = string.IsNullOrWhiteSpace(item.Author) ? string.Empty : $" from {item.Author}";

        var lines = item.Body.Replace("\r\n", "\n").Split('\n');
        var quoted = string.Join("\n", lines.Take(QuotedLines).Select(line => $"> {line}"));
        if (lines.Length > QuotedLines)
            quoted += "\n> …";

        return $"In reply to {what}{who}:\n\n{quoted}\n\n{answer}";
    }

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
    /// Who is queuing, as the queue records it: the run when there is one,
    /// otherwise the chat session — never both.
    /// </summary>
    private sealed record Caller(Guid? RunId, Guid? ChatSessionId)
    {
        public static Caller Of(Guid? runId, Guid? chatSessionId) => new(runId, runId is null ? chatSessionId : null);

        public bool Queued(PrQueuedWrite write) => write.IsQueuedBy(RunId, ChatSessionId);

        /// <summary>Only ever asked of a caller that owns a write, and so has one of the two.</summary>
        public string Name => RunId is not null ? $"run {RunId}" : $"chat session {ChatSessionId}";
    }

    /// <summary>
    /// What <see cref="PlaceAsync"/> did. <see cref="Id"/> is the write that now
    /// stands for the call — the one placed, or the resolve already waiting that
    /// made it unnecessary — or null when nothing is queued for it.
    /// <see cref="Replaced"/> and <see cref="Withdrawn"/> are the caller's own
    /// earlier write for the item, as it was before this call took its place or
    /// took it back.
    /// </summary>
    private sealed record Placement(
        RemotePrWriteResult? Refusal,
        string? Id = null,
        PrQueuedWrite? Replaced = null,
        PrQueuedWrite? Withdrawn = null,
        bool AlreadyQueued = false);

    /// <summary>
    /// Make <paramref name="write"/> the caller's pending word on
    /// <paramref name="item"/>: it takes the place — and the id — of whatever
    /// the caller already has queued for that item, or is appended; a null
    /// write takes that earlier one back. With no item it is simply appended.
    ///
    /// An item is its kind as well as the id the agent named it by: comment
    /// ids and review ids are separate counters, so the same string can name a
    /// review body when one answer is queued and a comment when the next one is
    /// (see <see cref="Find"/>), and matching on the string alone would let an
    /// answer to one overwrite the answer to the other.
    /// A resolve of a thread the caller already has waiting to be resolved is
    /// not queued twice.
    ///
    /// Every one of those decisions is taken inside the compare-and-set, against
    /// the queue as it stands when the write lands. Taken outside it, two
    /// corrections racing each other would each see nothing pending and both be
    /// appended — the pile of stale answers to one review this exists to end —
    /// and one the PR node claimed meanwhile would be written back.
    /// </summary>
    private async Task<Placement> PlaceAsync(
        LoopRun run, Caller caller, (string Kind, string Id)? item, PrQueuedWrite? write)
    {
        var intent = write is null
            ? null
            : write with
            {
                ItemId = item?.Id, ItemKind = item?.Kind,
                QueuedByRunId = caller.RunId, QueuedByChatSessionId = caller.ChatSessionId,
            };
        PrQueuedWrite? previous = null, waiting = null, placed = null;
        var full = false;
        var unchanged = false;
        var written = await MutateQueueAsync(run.Id, queued =>
        {
            previous = item is not { } answered
                ? null
                : queued.FirstOrDefault(q => string.Equals(q.ItemId, answered.Id, StringComparison.Ordinal)
                    && string.Equals(q.ItemKind, answered.Kind, StringComparison.Ordinal) && caller.Queued(q));
            waiting = intent?.Kind == PrQueuedWrite.Resolve
                ? queued.FirstOrDefault(q => q.Kind == PrQueuedWrite.Resolve && q.Id != previous?.Id
                    && string.Equals(q.TargetId, intent.TargetId, StringComparison.Ordinal) && caller.Queued(q))
                : null;
            placed = waiting is null ? intent : null;
            full = previous is null && placed is not null && queued.Count >= PrQueuedWrite.MaxQueued;
            unchanged = previous is null && placed is null;
            if (full || unchanged)
                return null;

            if (previous is null)
                return queued.Append(placed!).ToList();
            if (placed is null)
                return queued.Where(q => q.Id != previous.Id).ToList();
            placed = placed with { Id = previous.Id };
            return queued.Select(q => q.Id == previous.Id ? placed : q).ToList();
        });

        if (full)
            return new Placement(new RemotePrWriteResult(false, null,
                $"This run already has {PrQueuedWrite.MaxQueued} pull-request writes waiting for the PR node; nothing more is queued until they go out."));
        if (!written && !unchanged)
            return new Placement(new RemotePrWriteResult(false, null,
                "The queue is being changed from somewhere else; nothing in it was changed. Try again."));

        if (previous is not null && placed is not null)
            await RecordReplacedAsync(run.Id, caller, previous, placed);
        else if (previous is not null)
            await RecordWithdrawnAsync(run.Id, caller, previous);

        return new Placement(
            null,
            placed?.Id ?? waiting?.Id,
            Replaced: placed is null ? null : previous,
            Withdrawn: placed is null ? previous : null,
            AlreadyQueued: waiting is not null);
    }

    /// <summary>How a tool result names the caller's earlier write for an item.</summary>
    private static string Named(PrQueuedWrite write)
        => write.Kind == PrQueuedWrite.Resolve ? "close" : "answer";

    private sealed record Target(LoopRun Run, string RepoUrl, string PrNumber);

    /// <summary>The PR of the run the work item shows, live or ended: what reading its review looks at.</summary>
    private async Task<Target?> ReadTargetAsync(string workItemId)
        => TargetOf(await _runs.GetLatestByWorkItemAsync(workItemId));

    /// <summary>
    /// The PR of the work item's live run. A queued write waits for the PR node
    /// of the run it was queued on, and an ended run has no PR node left to send it.
    /// </summary>
    private async Task<Target?> WriteTargetAsync(string workItemId)
        => TargetOf(await _runs.GetActiveByWorkItemAsync(workItemId));

    private static Target? TargetOf(LoopRun? run)
    {
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
