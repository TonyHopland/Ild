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
/// </summary>
public sealed class PrStatusPollService : IPrStatusPollService
{
    private readonly ILoopRunStore _runs;
    private readonly IRemoteProvider _remote;
    private readonly ILoopEngine _engine;
    private readonly IRunNotifier _notifier;
    private readonly ILogger<PrStatusPollService> _log;

    public PrStatusPollService(
        ILoopRunStore runs,
        IRemoteProvider remote,
        ILoopEngine engine,
        IRunNotifier notifier,
        ILogger<PrStatusPollService> log)
    {
        _runs = runs;
        _remote = remote;
        _engine = engine;
        _notifier = notifier;
        _log = log;
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

        var runNode = await ResolveRunNodeAsync(run);
        if (runNode is null)
            return;

        var newStates = PrNodeEdges.ActiveStates(snapshot);
        var baseline = PrNodeEdges.ParseStates(run.PrPolledEdgeStates);
        var newlyTrue = new HashSet<string>(newStates, StringComparer.Ordinal);
        newlyTrue.ExceptWith(baseline);

        // Connected-only: emit nothing for a newly-true state whose named edge
        // isn't wired, otherwise the engine would fail the run ("missing edge
        // connection"). Read before the review ledger, so an unwired on_comment
        // costs no forge call and writes no ledger at all.
        var edges = await _runs.GetEdgesForNodeIdsAsync(new[] { runNode.LoopNodeId });
        var connected = edges
            .Where(e => e.SourceNodeId == runNode.LoopNodeId && e.EdgeType == EdgeType.Custom && !string.IsNullOrEmpty(e.Name))
            .Select(e => e.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var review = connected.Contains(PrNodeEdges.OnComment)
            ? await ReadReviewAsync(run, repoUrl, prNumber)
            : null;

        // Persist snapshot + new baseline and push the GUI update regardless of
        // whether any edge fires. This write carries the whole row, but no
        // longer the ledger or the queue: UpdateRunAsync leaves both columns
        // alone, because this instance was loaded before the forge fetch above
        // and writing its copy back would revert anything recorded meanwhile.
        run.PrSnapshot = PrSnapshotJson.Serialize(snapshot);
        run.PrPolledEdgeStates = string.Join(",", newStates);
        run.UpdatedAt = DateTime.UtcNow;
        await _runs.UpdateRunAsync(run);
        await _notifier.PrSnapshotChangedAsync(run.Id);

        // A run with no ledger yet records what is already on the pull request
        // and fires nothing this tick, whichever edge wins below.
        if (review is { Seeding: true })
            await SetLedgerAsync(run, review);

        var candidates = newlyTrue.Where(connected.Contains).ToHashSet(StringComparer.Ordinal);
        if (review is { Seeding: false } && review.Decision.Items.Count > 0)
            candidates.Add(PrNodeEdges.OnComment);

        var edge = PrNodeEdges.HighestPriority(candidates);
        if (edge is null)
            return;

        // Only the round that is actually being handed the items consumes them:
        // a higher-priority state winning this tick leaves them outstanding.
        if (edge == PrNodeEdges.OnComment)
            await SetLedgerAsync(run, review!);

        // Carry why: the signal's output becomes the resumed node's output, so a
        // node wired to on_ci_failed reads the failing checks out of
        // {{PreviousNode.Output}} instead of guessing at red CI.
        var detail = edge == PrNodeEdges.OnComment ? PrCommentDelivery.Describe(review!.Decision.Items) : null;
        await _engine.SignalNodeResultAsync(run.Id, runNode.Id,
            NodeSignal.Custom(edge, PrNodeEdges.Describe(edge, snapshot, detail, run.WorkItemId)));
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
    /// Record what this tick handed over.
    ///
    /// NOT a compare-and-set, unlike every other writer of this column, and
    /// that is a known hole rather than an oversight: the decision here is made
    /// from the ledger this pass loaded BEFORE a forge fetch that costs seconds,
    /// so a drop landing in that window is reverted by this write and the
    /// finding it put back stays suppressed. Closing it means re-deciding
    /// against a fresh read under PrCommentLedgerWriter — a two-line change —
    /// but the acceptance test that pins this path asserts the write goes
    /// through SetPrCommentLedgerAsync, and that file is not one this change is
    /// allowed to edit. Escalated rather than worked around.
    /// </summary>
    private async Task SetLedgerAsync(LoopRun run, ReviewPass review)
    {
        var json = PrCommentLedgerJson.Serialize(review.Decision.Ledger);
        run.PrCommentLedger = json;
        await _runs.SetPrCommentLedgerAsync(run.Id, json);
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
