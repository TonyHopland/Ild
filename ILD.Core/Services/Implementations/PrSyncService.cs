using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class PrSyncService : IPrSyncService
{
    private readonly ILoopRunStore _loopRunStore;
    private readonly IEventLogStore _eventLogStore;
    private readonly IWorkItemManager _workItems;

    public PrSyncService(ILoopRunStore loopRunStore, IEventLogStore eventLogStore, IWorkItemManager workItems)
    {
        _loopRunStore = loopRunStore;
        _eventLogStore = eventLogStore;
        _workItems = workItems;
    }

    public async Task HandleWebhookAsync(WebhookPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.PullRequestUrl)) return;
        var run = await _loopRunStore.GetByPrUrlAsync(payload.PullRequestUrl);
        if (run == null) return;

        if (string.Equals(payload.MergeStatus, "merged", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(payload.EventType, "pull_request.merged", StringComparison.OrdinalIgnoreCase))
        {
            run.IsPrMerged = true;
            run.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(run);
            await _workItems.ManuallyMarkMergedAsync(run.WorkItemId);
        }

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
}
