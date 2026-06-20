using System.Text.Json;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
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
/// </summary>
public sealed class PrStatusPollService : IPrStatusPollService
{
    // Snapshot is serialised camelCase so the feedback UI consumes it directly.
    private static readonly JsonSerializerOptions SnapshotJson = JsonSerializerOptions.Web;

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

        // Persist snapshot + new baseline and push the GUI update regardless of
        // whether any edge fires.
        run.PrSnapshot = JsonSerializer.Serialize(snapshot, SnapshotJson);
        run.PrPolledEdgeStates = string.Join(",", newStates);
        run.UpdatedAt = DateTime.UtcNow;
        await _runs.UpdateRunAsync(run);
        await _notifier.PrSnapshotChangedAsync(run.Id);

        if (newlyTrue.Count == 0)
            return;

        // Connected-only: emit nothing for a newly-true state whose named edge
        // isn't wired, otherwise the engine would fail the run ("missing edge
        // connection"). Among connected + newly-true, fire the highest priority.
        var edges = await _runs.GetEdgesForNodeIdsAsync(new[] { runNode.LoopNodeId });
        var connected = edges
            .Where(e => e.SourceNodeId == runNode.LoopNodeId && e.EdgeType == EdgeType.Custom && !string.IsNullOrEmpty(e.Name))
            .Select(e => e.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var edge = PrNodeEdges.HighestPriority(newlyTrue.Where(connected.Contains));
        if (edge is null)
            return;

        await _engine.SignalNodeResultAsync(run.Id, runNode.Id, NodeSignal.Custom(edge));
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
