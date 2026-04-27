using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
namespace ILD.Core.Services.Interfaces;

public interface IRemoteProvider
{
    Task<RemotePrResult> CreatePullRequestAsync(string repoUrl, string sourceBranch, string targetBranch, string title, string body);
    Task<bool> MergePullRequestAsync(string repoUrl, string prNumber);
    Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(string repoUrl, string prNumber);
    Task RegisterWebhookAsync(string repoUrl, string callbackUrl);
    Task UnregisterWebhookAsync(string repoUrl, string callbackUrl);
    Task<RemotePrStatus> GetPullRequestStatusAsync(string repoUrl, string prNumber);
    Task<bool> DeleteBranchAsync(string repoUrl, string branchName);
}
