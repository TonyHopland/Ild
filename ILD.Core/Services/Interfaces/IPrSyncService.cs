using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IPrSyncService
{
    Task HandleWebhookAsync(WebhookPayload payload);
    Task<bool> IsPullRequestMergedAsync(string prUrl);
    Task SyncPullRequestCommentsAsync(string workItemId, string prUrl);
    Task RegisterWorkItemPrLinkAsync(string workItemId, string prUrl);
    Task<string?> GetPrUrlForWorkItemAsync(string workItemId);
}
