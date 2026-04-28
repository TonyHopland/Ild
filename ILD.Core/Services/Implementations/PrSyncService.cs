using ILD.Core.DTOs;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public class PrSyncService : IPrSyncService
{
    private readonly AppDbContext _db;
    private readonly IWorkItemManager _workItems;

    public PrSyncService(AppDbContext db, IWorkItemManager workItems)
    {
        _db = db;
        _workItems = workItems;
    }

    public async Task HandleWebhookAsync(WebhookPayload payload)
    {
        if (payload == null || string.IsNullOrEmpty(payload.PullRequestUrl)) return;
        var wi = await _db.WorkItems.FirstOrDefaultAsync(w => w.PrUrl == payload.PullRequestUrl);
        if (wi == null) return;

        if (string.Equals(payload.MergeStatus, "merged", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(payload.EventType, "pull_request.merged", StringComparison.OrdinalIgnoreCase))
        {
            await _workItems.ManuallyMarkMergedAsync(wi.Id);
        }

        if (!string.IsNullOrEmpty(payload.Comment))
        {
            _db.EventLogs.Add(new EventLog
            {
                Id = Guid.NewGuid(),
                LoopRunId = wi.CurrentLoopRunId,
                EventType = EventType.HumanFeedbackReceived,
                Data = payload.Comment,
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
        }
    }

    public Task<bool> IsPullRequestMergedAsync(string prUrl) => Task.FromResult(false);

    public Task SyncPullRequestCommentsAsync(Guid workItemId, string prUrl) => Task.CompletedTask;

    public async Task RegisterWorkItemPrLinkAsync(Guid workItemId, string prUrl)
    {
        var wi = await _db.WorkItems.FindAsync(workItemId);
        if (wi == null) return;
        wi.PrUrl = prUrl;
        wi.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<string?> GetPrUrlForWorkItemAsync(Guid workItemId)
    {
        var wi = await _db.WorkItems.FindAsync(workItemId);
        return wi?.PrUrl;
    }
}
