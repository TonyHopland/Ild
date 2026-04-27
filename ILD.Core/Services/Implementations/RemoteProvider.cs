using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class RemoteProvider : IRemoteProvider
{
    private readonly ILogger<RemoteProvider> _logger;
    private readonly AppDbContext _dbContext;

    public RemoteProvider(ILogger<RemoteProvider> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<RemotePrResult> CreatePullRequestAsync(string repoUrl, string sourceBranch, string targetBranch, string title, string body)
    {
        throw new NotImplementedException(nameof(CreatePullRequestAsync));
    }

    public Task<bool> MergePullRequestAsync(string repoUrl, string prNumber)
    {
        throw new NotImplementedException(nameof(MergePullRequestAsync));
    }

    public Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(string repoUrl, string prNumber)
    {
        throw new NotImplementedException(nameof(GetPullRequestCommentsAsync));
    }

    public Task RegisterWebhookAsync(string repoUrl, string callbackUrl)
    {
        throw new NotImplementedException(nameof(RegisterWebhookAsync));
    }

    public Task UnregisterWebhookAsync(string repoUrl, string callbackUrl)
    {
        throw new NotImplementedException(nameof(UnregisterWebhookAsync));
    }

    public Task<RemotePrStatus> GetPullRequestStatusAsync(string repoUrl, string prNumber)
    {
        throw new NotImplementedException(nameof(GetPullRequestStatusAsync));
    }

    public Task<bool> DeleteBranchAsync(string repoUrl, string branchName)
    {
        throw new NotImplementedException(nameof(DeleteBranchAsync));
    }
}
