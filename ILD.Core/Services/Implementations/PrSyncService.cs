using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class PrSyncService : IPrSyncService
{
    private readonly ILogger<PrSyncService> _logger;
    private readonly AppDbContext _dbContext;

    public PrSyncService(ILogger<PrSyncService> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task HandleWebhookAsync(WebhookPayload payload)
    {
        throw new NotImplementedException(nameof(HandleWebhookAsync));
    }

    public Task<bool> IsPullRequestMergedAsync(string prUrl)
    {
        throw new NotImplementedException(nameof(IsPullRequestMergedAsync));
    }

    public Task SyncPullRequestCommentsAsync(Guid workItemId, string prUrl)
    {
        throw new NotImplementedException(nameof(SyncPullRequestCommentsAsync));
    }

    public Task RegisterWorkItemPrLinkAsync(Guid workItemId, string prUrl)
    {
        throw new NotImplementedException(nameof(RegisterWorkItemPrLinkAsync));
    }

    public Task<string?> GetPrUrlForWorkItemAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(GetPrUrlForWorkItemAsync));
    }
}
