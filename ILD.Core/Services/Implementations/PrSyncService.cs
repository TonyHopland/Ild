using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class PrSyncService : IPrSyncService
{
    private readonly IWorkItemStore _workItemStore;
    private readonly IEventLogStore _eventLogStore;
    private readonly IWorkItemManager _workItems;

    public PrSyncService(IWorkItemStore workItemStore, IEventLogStore eventLogStore, IWorkItemManager workItems)
    {
        _workItemStore = workItemStore;
        _eventLogStore = eventLogStore;
        _workItems = workItems;
    }

    public async Task HandleWebhookAsync(WebhookPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.PullRequestUrl)) return;
        var wi = await FindByPrUrlAsync(payload.PullRequestUrl);
        if (wi == null) return;

        if (string.Equals(payload.MergeStatus, "merged", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(payload.EventType, "pull_request.merged", StringComparison.OrdinalIgnoreCase))
        {
            await _workItems.ManuallyMarkMergedAsync(wi.Id);
        }

        if (!string.IsNullOrEmpty(payload.Comment) && wi.CurrentLoopRunId.HasValue)
        {
            await _eventLogStore.AppendAsync(new EventLog
            {
                Id = Guid.NewGuid(),
                LoopRunId = wi.CurrentLoopRunId.Value,
                EventType = EventType.HumanFeedbackReceived,
                Data = payload.Comment,
                Timestamp = DateTime.UtcNow,
            });
        }
    }

    public Task<bool> IsPullRequestMergedAsync(string prUrl) => Task.FromResult(false);

    public Task SyncPullRequestCommentsAsync(Guid workItemId, string prUrl) => Task.CompletedTask;

    public async Task RegisterWorkItemPrLinkAsync(Guid workItemId, string prUrl)
    {
        var wi = await _workItemStore.GetByIdAsync(workItemId);
        if (wi == null) return;
        wi.PrUrl = prUrl;
        wi.UpdatedAt = DateTime.UtcNow;
        await _workItemStore.UpdateAsync(wi);
    }

    public async Task<string?> GetPrUrlForWorkItemAsync(Guid workItemId)
    {
        var wi = await _workItemStore.GetByIdAsync(workItemId);
        return wi?.PrUrl;
    }

    private async Task<WorkItem?> FindByPrUrlAsync(string prUrl)
    {
        foreach (var status in Enum.GetValues<WorkItemStatus>())
        {
            foreach (var w in await _workItemStore.GetByStatusAsync(status))
            {
                if (w.PrUrl == prUrl)
                    return w;
            }
        }
        return null;
    }
}
