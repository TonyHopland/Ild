using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Implementations;

public class PrSyncService : IPrSyncService
{
    private readonly ILoopRunStore _loopRunStore;
    private readonly IEventLogStore _eventLogStore;
    private readonly IWorkItemManager _workItems;
    private readonly ILoopEngine _loopEngine;

    public PrSyncService(ILoopRunStore loopRunStore, IEventLogStore eventLogStore, IWorkItemManager workItems, ILoopEngine loopEngine)
    {
        _loopRunStore = loopRunStore;
        _eventLogStore = eventLogStore;
        _workItems = workItems;
        _loopEngine = loopEngine;
    }

    public async Task HandleWebhookAsync(WebhookPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.PullRequestUrl)) return;
        var run = await _loopRunStore.GetByPrUrlAsync(payload.PullRequestUrl);
        if (run == null) return;

        if (!string.IsNullOrEmpty(payload.Comment))
        {
            await _eventLogStore.AppendAsync(new EventLog
            {
                Id = Guid.NewGuid(),
                LoopRunId = run.Id,
                EventType = EventType.HumanFeedbackReceived,
                Data = payload.Comment,
                Timestamp = DateTime.UtcNow,
            });
        }

        var edgeName = MapWebhookToEdge(payload, out var merged);

        if (merged)
        {
            // Per-run PRs (ADR-0008) mean a webhook can arrive for an *old*
            // run's still-open PR. Merge bookkeeping must stay on the run that
            // owns the PR — tagging whatever run is "current" instead could
            // mark the wrong run merged and flip the item to Done while another
            // run is mid-flight.
            run.IsPrMerged = true;
            run.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(run);

            // An active run completes through its own graph (PR node signal →
            // ... → Cleanup → Done). Only when the merged PR belongs to the
            // work item's current run and that run is already terminal (no
            // engine path left to resume) does the merge finish the item here.
            if (run.Status is LoopRunStatus.Failed or LoopRunStatus.Cancelled)
            {
                var current = await _loopRunStore.GetCurrentByWorkItemAsync(run.WorkItemId);
                if (current?.Id == run.Id)
                    await _workItems.TransitionAsync(run.WorkItemId, RemoteWorkItemStatus.Done,
                        currentLoopRunId: run.Id);
            }
        }

        if (edgeName == null)
            return;

        var runNode = await ResolveRunNodeAsync(run);
        if (runNode == null)
            return;

        // Connected-only (mirrors the PR heartbeat poller): a named custom edge
        // with no wired connection fails the run ("missing edge connection"), so
        // emit only when the PR node actually wires that edge. No fallback to
        // OnSuccess/OnFailure for any state.
        var edges = await _loopRunStore.GetEdgesForNodeIdsAsync(new[] { runNode.LoopNodeId });
        var connected = edges.Any(e => e.SourceNodeId == runNode.LoopNodeId
            && e.EdgeType == EdgeType.Custom
            && string.Equals(e.Name, edgeName, StringComparison.Ordinal));
        if (!connected)
            return;

        await _loopEngine.SignalNodeResultAsync(run.Id, runNode.Id, NodeSignal.Custom(edgeName));
    }

    public Task<bool> IsPullRequestMergedAsync(string prUrl) => Task.FromResult(false);

    public Task SyncPullRequestCommentsAsync(string workItemId, string prUrl) => Task.CompletedTask;

    public async Task RegisterWorkItemPrLinkAsync(string workItemId, string prUrl)
    {
        var run = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        if (run == null) return;
        run.PrUrl = prUrl;
        run.UpdatedAt = DateTime.UtcNow;
        await _loopRunStore.UpdateRunAsync(run);
    }

    public async Task<string?> GetPrUrlForWorkItemAsync(string workItemId)
    {
        var run = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        return run?.PrUrl;
    }

    /// <summary>
    /// Map a webhook payload to the PR-node custom edge it should fire, and
    /// report whether it represents a merge (for IsPrMerged bookkeeping).
    /// Mirrors the heartbeat poller's edge vocabulary so both resume paths stay
    /// consistent: changes-requested → <c>on_rejected</c>, closed-without-merge
    /// → <c>on_abandoned</c>, merged → <c>on_merged</c>.
    /// </summary>
    private static string? MapWebhookToEdge(WebhookPayload payload, out bool merged)
    {
        merged = string.Equals(payload.MergeStatus, "merged", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.EventType, "pull_request.merged", StringComparison.OrdinalIgnoreCase);
        if (merged)
            return PrNodeEdges.OnMerged;

        if (string.Equals(payload.MergeStatus, "changes_requested", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.MergeStatus, "rejected", StringComparison.OrdinalIgnoreCase))
            return PrNodeEdges.OnRejected;

        if (string.Equals(payload.MergeStatus, "closed", StringComparison.OrdinalIgnoreCase))
            return PrNodeEdges.OnAbandoned;

        return null;
    }

    private async Task<LoopRunNode?> ResolveRunNodeAsync(LoopRun run)
    {
        if (run.CurrentNodeId.HasValue)
        {
            var current = await _loopRunStore.GetRunNodeAsync(run.Id, run.CurrentNodeId.Value);
            if (current != null && (current.Status == LoopRunNodeStatus.WaitingHuman || current.Status == LoopRunNodeStatus.Failed))
                return current;
        }

        var runNodes = await _loopRunStore.GetRunNodesAsync(run.Id);
        return runNodes
            .Where(node => node.Status == LoopRunNodeStatus.WaitingHuman || node.Status == LoopRunNodeStatus.Failed)
            .OrderByDescending(node => node.StartedAt ?? node.CreatedAt)
            .FirstOrDefault();
    }
}
