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

        var signal = GetSignal(payload);
        if (signal == null)
            return;

        if (signal.Type == ExternalActionResultType.Success)
        {
            // Per-run PRs (ADR-0008) mean a webhook can arrive for an *old*
            // run's still-open PR. Merge bookkeeping must stay on the run that
            // owns the PR — going through the work item (ManuallyMarkMergedAsync)
            // would mark the current run merged instead and could flip the item
            // to Done while another run is mid-flight.
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

        var runNode = await ResolveRunNodeAsync(run);
        if (runNode == null)
            return;

        await _loopEngine.SignalNodeResultAsync(run.Id, runNode.Id, signal);
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

    private static NodeSignal? GetSignal(WebhookPayload payload)
    {
        if (string.Equals(payload.MergeStatus, "merged", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.EventType, "pull_request.merged", StringComparison.OrdinalIgnoreCase))
            return NodeSignal.Success();

        if (string.Equals(payload.EventType, "pull_request.rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.MergeStatus, "closed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.MergeStatus, "changes_requested", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payload.MergeStatus, "rejected", StringComparison.OrdinalIgnoreCase))
            return NodeSignal.Reject(payload.Comment ?? "PR rejected");

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
