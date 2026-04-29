using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IPrSyncService
{
    Task HandleWebhookAsync(WebhookPayload payload);
    Task<bool> IsPullRequestMergedAsync(string prUrl);
    Task SyncPullRequestCommentsAsync(Guid workItemId, string prUrl);
    Task RegisterWorkItemPrLinkAsync(Guid workItemId, string prUrl);
    Task<string?> GetPrUrlForWorkItemAsync(Guid workItemId);
}
