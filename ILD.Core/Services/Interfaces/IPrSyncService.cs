using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
namespace ILD.Core.Services.Interfaces;

public interface IPrSyncService
{
    Task HandleWebhookAsync(WebhookPayload payload);
    Task<bool> IsPullRequestMergedAsync(string prUrl);
    Task SyncPullRequestCommentsAsync(Guid workItemId, string prUrl);
    Task RegisterWorkItemPrLinkAsync(Guid workItemId, string prUrl);
    Task<string?> GetPrUrlForWorkItemAsync(Guid workItemId);
}
