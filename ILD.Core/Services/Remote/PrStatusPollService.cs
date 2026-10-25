using ILD.Data;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Remote;

/// <summary>One pass of PR-heartbeat polling. Scoped (owns a DbContext via the stores).</summary>
public interface IPrStatusPollService
{
    Task PollOnceAsync(CancellationToken ct = default);
}

/// <summary>
/// Fetches a fresh PR snapshot for every run parked in <c>PrAwaitingMerge</c>,
/// persists it (driving the feedback UI), and — for the single highest-priority
/// state that newly became true this tick and whose custom edge is actually
/// connected on the PR node — emits a <see cref="NodeSignal.Custom"/> so the
/// engine routes the run away. Unconnected or already-true states only refresh
/// the snapshot. There is no fallback to OnSuccess/OnFailure. See
/// <see cref="PrNodeEdges"/> and the PR Node entry in CONTEXT.md.
///
/// <c>on_comment</c> joins them from a different evidence base: not a state of
/// the snapshot but the run's own delivery ledger, decided by
/// <see cref="PrCommentDelivery"/>. This is the one place every throttle on it
/// is applied, which is why the comment webhook only wakes this pass rather than
/// firing an edge of its own.
///
/// It is also where ILD merges an AutoMerge pull request whose provider could
/// not turn its own auto-merge on: once, on the first tick the pull request may
/// be merged, ahead of every edge. See <see cref="TryAutoMergeAsync"/>.
/// </summary>
public sealed class PrStatusPollService : IPrStatusPollService
{
    private readonly ILoopRunStore _runs;
    private readonly IRemoteProvider _remote;
    private readonly ILoopEngine _engine;
    private readonly IRunNotifier _notifier;
    private readonly ILogger<PrStatusPollService> _log;
    private readonly IWorkItemManager? _workItems;
    private readonly IEventLogService? _eventLog;
    private readonly IWorkItemNotifier? _boardNotifier;

    public PrStatusPollService(
        ILoopRunStore runs,
        IRemoteProvider remote,
        ILoopEngine engine,
        IRunNotifier notifier,
        ILogger<PrStatusPollService> log,
        IWorkItemManager? workItems = null,
        IEventLogService? eventLog = null,
        IWorkItemNotifier? boardNotifier = null)
    {
        _runs = runs;
        _remote = remote;
        _engine = engine;
        _notifier = notifier;
        _log = log;
        _workItems = workItems;
        _eventLog = eventLog;
        _boardNotifier = boardNotifier;
    }

    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var runs = await _runs.GetPrAwaitingMergeRunsAsync();
        foreach (var run in runs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PollRunAsync(run, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PR heartbeat failed for run {RunId}", run.Id);
            }
        }
    }

    private async Task PollRunAsync(LoopRun run, CancellationToken ct)
    {
        var repoUrl = RemotePrUrl.ExtractRepoUrl(run.PrUrl);
        var prNumber = RemotePrUrl.ExtractPrNumber(run.PrUrl);
        if (repoUrl is null || prNumber is null)
            return;

        var snapshot = await _remote.GetPullRequestSnapshotAsync(repoUrl, prNumber);
        if (snapshot is null)
            return;

        // A push by the PR node cleared the stored result and recorded the commit it
        // pushed. Until the forge reports that commit as the head, this read may still
        // describe the previous one, so the whole tick waits: nothing stored, no edge,
        // no merge, no event. There is no timeout; a head that never matches (a force
        // push over that commit) leaves the card blank until a person steps in.
        if (run.PrPushedCommit is { } pushed)
        {
            if (!string.Equals(snapshot.HeadSha, pushed, StringComparison.OrdinalIgnoreCase))
                return;
            await _runs.ClearPrPushedCommitAsync(run.Id, pushed);
            run.PrPushedCommit = null;
        }

        var runNode = await ResolveRunNodeAsync(run);
        if (runNode is null)
            return;

        var newStates = PrNodeEdges.ActiveStates(snapshot);
        var baseline = PrNodeEdges.ParseStates(run.PrPolledEdgeStates);
        var newlyTrue = new HashSet<string>(newStates, StringComparer.Ordinal);
        newlyTrue.ExceptWith(baseline);

        // Connected-only: emit nothing for a newly-true state whose named edge
        // isn't wired, otherwise the engine would park the run ("missing edge
        // connection"). Read before the review ledger, so an unwired on_comment
        // costs no forge call and writes no ledger at all.
        var edges = await _runs.GetEdgesForNodeIdsAsync(new[] { runNode.LoopNodeId });
        var connected = edges
            .Where(e => e.SourceNodeId == runNode.LoopNodeId && e.EdgeType == EdgeType.Custom && !string.IsNullOrEmpty(e.Name))
            .Select(e => e.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var review = connected.Contains(LoopOutputs.OnComment)
            ? await ReadReviewAsync(run, repoUrl, prNumber)
            : null;

        // Persist snapshot + new baseline and push the GUI update regardless of
        // whether any edge fires. This instance was loaded before the forge fetch above,
        // which takes seconds, and a person may have answered the parked node in that
        // window. So the write is targeted: the three columns this pass owns, and only
        // while the run is still parked at this PR node. The instance is never saved as
        // a whole row, here or later in the pass; that would put its stale position back
        // over the engine's and leave the run waiting on a node nobody can answer. If
        // nothing was written the run has moved on, and the pass ends without touching it.
        var badgesBefore = WorkItemPrStatus.From(PrSnapshotJson.TryParse(run.PrSnapshot));
        var baselineBefore = run.PrPolledEdgeStates;
        var storedSnapshot = PrSnapshotJson.Serialize(snapshot);
        var storedStates = string.Join(",", newStates);
        if (!await _runs.TryRecordPrPollAsync(run.Id, runNode.LoopNodeId, storedSnapshot, storedStates))
            return;
        // Later code in this pass reads these; the store has already written them.
        run.PrSnapshot = storedSnapshot;
        run.PrPolledEdgeStates = storedStates;
        await _notifier.PrSnapshotChangedAsync(run.Id);
        if (_boardNotifier is not null && badgesBefore != WorkItemPrStatus.From(snapshot))
            await _boardNotifier.PrStatusChangedAsync(run.WorkItemId);

        // A run with no ledger yet records what is already on the pull request
        // and fires nothing this tick, whichever edge wins below.
        if (review is { Seeding: true })
            await RecordHandoverAsync(run, review);

        // Ahead of the edges, so on the tick the pull request may be merged one
        // that went true alongside (a green CI, an approval) cannot take the
        // run off the PR node before ILD has merged. This snapshot still shows
        // the PR open, so a merge fires nothing now: the next tick reads it
        // merged and fires on_merged like any other merge.
        switch (await TryAutoMergeAsync(run, snapshot, repoUrl, prNumber))
        {
            case AutoMergeOutcome.Merged:
                return;
            case AutoMergeOutcome.HeadMoved:
                // This snapshot, and the edge states worked out from it, belong to a commit that is
                // no longer the head. Take them back out: the stored result is cleared rather than
                // restored (what it replaces described an older commit too, and the card shows
                // nothing rather than a guess), and the baseline is put back so the new head's
                // first poll still sees its states as newly true. No edge fires on this tick.
                if (!await _runs.TryRecordPrPollAsync(run.Id, runNode.LoopNodeId, null, baselineBefore))
                    return;
                run.PrSnapshot = null;
                run.PrPolledEdgeStates = baselineBefore;
                await _notifier.PrSnapshotChangedAsync(run.Id);
                if (_boardNotifier is not null)
                    await _boardNotifier.PrStatusChangedAsync(run.WorkItemId);
                return;
        }

        var candidates = newlyTrue.Where(connected.Contains).ToHashSet(StringComparer.Ordinal);
        if (review is { Seeding: false } && review.Decision.Items.Count > 0)
            candidates.Add(LoopOutputs.OnComment);

        // Only the round that is actually being handed the items consumes them:
        // a higher-priority state winning this tick leaves them outstanding.
        string? detail = null;
        if (PrNodeEdges.HighestPriority(candidates) == LoopOutputs.OnComment)
        {
            // Describe what the WRITE handed over, not what this pass decided
            // before the forge fetch. The write re-decides against the ledger as
            // it stands, so the two disagree whenever the row moved inside that
            // window — a drop putting a finding back, another writer taking one.
            // Announcing the older batch records items as delivered that the
            // agent was never told about, and on this head they never fire again.
            // A write that never landed recorded nothing, so it consumed
            // nothing, and this tick behaves exactly as it did before there was
            // an effective batch to ask about.
            var handed = await RecordHandoverAsync(run, review!) ?? review!.Decision.Items;
            if (handed.Count == 0)
            {
                // The batch was gone by the time the write ran — another writer
                // took it inside the window. Firing on_comment now would start a
                // round on a reason naming findings it will never be handed, so
                // it drops out of the running; but returning outright would
                // strand whatever else went true this tick. The baseline was
                // saved above, so an approval or a green CI that lost the
                // priority contest to a batch which then vanished is already
                // counted as seen, and nothing clears that until a re-park.
                candidates.Remove(LoopOutputs.OnComment);
            }
            else
            {
                detail = PrCommentDelivery.Describe(handed);
            }
        }

        var edge = PrNodeEdges.HighestPriority(candidates);
        if (edge is null)
            return;

        // Carry why: the signal's output becomes the resumed node's output, so a
        // node wired to on_ci_failed reads the failing checks out of
        // {{PreviousNode.Output}} instead of guessing at red CI.
        await _engine.SignalNodeResultAsync(run.Id, runNode.Id,
            NodeSignal.Custom(edge, PrNodeEdges.Describe(edge, snapshot, detail, run.WorkItemId)));
    }

    /// <summary>What <see cref="TryAutoMergeAsync"/> did: merged, found the head moved on, or did not merge.</summary>
    private enum AutoMergeOutcome { NotMerged, Merged, HeadMoved }

    /// <summary>
    /// ILD's one merge attempt on an AutoMerge pull request the provider will
    /// not merge by itself (<see cref="PrAutoMerge.IldPending"/>), made once the
    /// pull request may be merged and its work item still carries the tag.
    /// Reports whether it merged, or that the head moved on before it could.
    ///
    /// The attempt is claimed on the row before the provider is called, so it
    /// happens at most once whatever else runs or restarts. A failed attempt is
    /// never repeated: the run stays parked where it is, and a human is asked to
    /// merge by hand — which the heartbeat then sees as it sees any merge.
    /// </summary>
    private async Task<AutoMergeOutcome> TryAutoMergeAsync(LoopRun run, RemotePrSnapshot snapshot, string repoUrl, string prNumber)
    {
        if (run.PrAutoMerge != PrAutoMerge.IldPending || !MayMerge(snapshot) || _workItems is null)
            return AutoMergeOutcome.NotMerged;

        // The merge names the commit whose CI this snapshot read, so a forge that did not say which
        // commit that was leaves nothing to name: wait for a snapshot that does.
        if (snapshot.HeadSha is not { Length: > 0 } checkedHead)
            return AutoMergeOutcome.NotMerged;

        WorkItemView? workItem;
        try
        {
            workItem = await _workItems.GetWorkItemAsync(run.WorkItemId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AutoMerge: could not read work item {WorkItemId} for run {RunId}; trying again next tick",
                run.WorkItemId, run.Id);
            return AutoMergeOutcome.NotMerged;
        }
        if (workItem is null
            || !workItem.Tags.Any(t => string.Equals(t, PRNodeExecutor.AutoMergeTag, StringComparison.OrdinalIgnoreCase)))
            return AutoMergeOutcome.NotMerged;

        if (!await _runs.TrySetPrAutoMergeAsync(run.Id, PrAutoMerge.IldPending, PrAutoMerge.IldMerging))
            return AutoMergeOutcome.NotMerged;

        string? failure = null;
        try
        {
            if (!await _remote.MergePullRequestAsync(repoUrl, prNumber, checkedHead))
                failure = "the provider refused or could not complete the merge";
        }
        catch (RemoteHeadMovedException moved)
        {
            // Nothing was merged and the commit that was checked is no longer the head: not a failed
            // attempt. Hand it back so a later tick checks the new head's CI.
            _log.LogInformation(
                "AutoMerge: head of {PrUrl} moved from {Expected} to {Current} before the merge; waiting for CI on the new head",
                run.PrUrl, moved.Expected, moved.Current ?? "an unknown commit");
            await TrySetAutoMergeAsync(run.Id, PrAutoMerge.IldPending, null);
            return AutoMergeOutcome.HeadMoved;
        }
        catch (Exception ex)
        {
            failure = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        if (failure is not null)
        {
            await TrySetAutoMergeAsync(run.Id, PrAutoMerge.IldFailed, failure);
            await _eventLog.TryAppendAsync(run.Id, EventType.PrMergeFailed,
                $"AutoMerge: ILD could not merge this PR ({failure}). Please merge it manually.", _log);
            await _notifier.PrSnapshotChangedAsync(run.Id);
            return AutoMergeOutcome.NotMerged;
        }

        await TrySetAutoMergeAsync(run.Id, PrAutoMerge.IldMerged, null);
        await _eventLog.TryAppendAsync(run.Id, EventType.PrMerged, $"PR {run.PrUrl} merged by ILD (AutoMerge)", _log);
        await _notifier.PrSnapshotChangedAsync(run.Id);

        var branch = run.BranchName ?? workItem.BranchName ?? RunWorktreeNaming.BranchFor(workItem.Id, run.Id);
        await MergedBranch.TryDeleteAsync(_remote, _eventLog, _log, run.Id, repoUrl, branch);
        return AutoMergeOutcome.Merged;
    }

    /// <summary>
    /// Open, green and free to merge: every check finished and none failed (and
    /// at least one exists), no conflict, not a draft, and nothing the provider
    /// reports as still standing in the way, such as a required review.
    /// </summary>
    private static bool MayMerge(RemotePrSnapshot snapshot)
        => snapshot is { State: "open", Merged: false, Draft: false, Ci: RemotePrCiStatus.Passed, Mergeable: true, MergeBlocked: false }
            && !string.Equals(snapshot.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Settle the attempt this pass holds the claim on. Nothing else moves the
    /// state off <see cref="PrAutoMerge.IldMerging"/>, so a write that does not
    /// land leaves it there — never retried, which is the side to fail on.
    /// </summary>
    private async Task TrySetAutoMergeAsync(Guid runId, PrAutoMerge outcome, string? error)
    {
        try
        {
            await _runs.TrySetPrAutoMergeAsync(runId, PrAutoMerge.IldMerging, outcome, error);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AutoMerge: could not record {Outcome} on run {RunId}", outcome, runId);
        }
    }

    /// <summary>Whether this tick is the run's first look at the pull request, and what it found.</summary>
    private sealed record ReviewPass(RemotePrReviewLedger Fetched, PrCommentDecision Decision, bool Seeding);

    private async Task<ReviewPass?> ReadReviewAsync(LoopRun run, string repoUrl, string prNumber)
    {
        var fetched = await _remote.GetPullRequestReviewLedgerAsync(repoUrl, prNumber);
        if (!string.IsNullOrEmpty(fetched.Message))
        {
            // Nothing was read, so there is nothing to record. Seeding from a
            // failed fetch would claim the run had seen an empty pull request.
            _log.LogDebug("PR review ledger unavailable for run {RunId}: {Message}", run.Id, fetched.Message);
            return null;
        }

        var state = PrCommentLedgerJson.TryParse(run.PrCommentLedger);
        return new ReviewPass(fetched, PrCommentDelivery.Decide(fetched, fetched.HeadSha, state), state is null);
    }

    /// <summary>
    /// Record what this tick hands over, against the ledger as it stands now
    /// rather than the copy this pass loaded before it went to the forge.
    ///
    /// That fetch costs seconds, and a person dropping a queued answer inside
    /// it puts a finding back. Writing the pre-computed ledger would revert
    /// that and leave the finding suppressed, so the decision is re-made
    /// against whatever the row says now — safe because it is a pure function
    /// of (what the forge said, the ledger).
    ///
    /// Which is exactly why the caller needs what came back: the re-decision is
    /// the one that happened, so it is the only honest answer to "what was this
    /// round handed". Reports the items the winning write recorded, or null when
    /// no write landed at all.
    /// </summary>
    private async Task<IReadOnlyList<RemotePrReviewItem>?> RecordHandoverAsync(LoopRun run, ReviewPass review)
    {
        IReadOnlyList<RemotePrReviewItem> handed = Array.Empty<RemotePrReviewItem>();
        var recorded = await PrCommentLedgerWriter.MutateAsync(_runs, run.Id, state =>
        {
            var decision = PrCommentDelivery.Decide(review.Fetched, review.Fetched.HeadSha, state);
            handed = decision.Items;
            return decision.Ledger;
        });
        return recorded ? handed : null;
    }

    private async Task<LoopRunNode?> ResolveRunNodeAsync(LoopRun run)
    {
        if (run.CurrentNodeId.HasValue)
        {
            var current = await _runs.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value);
            if (current is { Status: LoopRunNodeStatus.WaitingHuman })
                return current;
        }

        var runNodes = await _runs.GetRunNodesAsync(run.Id);
        return runNodes
            .Where(node => node.Status == LoopRunNodeStatus.WaitingHuman)
            .OrderByDescending(node => node.StartedAt ?? node.CreatedAt)
            .FirstOrDefault();
    }
}
