using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IRemoteProvider
{
    Task<RemotePrResult> CreatePullRequestAsync(string repoUrl, string sourceBranch, string targetBranch, string title, string body);
    Task<bool> MergePullRequestAsync(string repoUrl, string prNumber);
    Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(string repoUrl, string prNumber);
    Task RegisterWebhookAsync(string repoUrl, string callbackUrl);
    Task UnregisterWebhookAsync(string repoUrl, string callbackUrl);
    Task<RemotePrStatus> GetPullRequestStatusAsync(string repoUrl, string prNumber);

    /// <summary>
    /// Fetch a full PR snapshot (title, description, mergeability, CI verdict,
    /// review decision, conversation). Returns null when the PR cannot be
    /// resolved or fetched. Backs the PR heartbeat poller and the feedback UI.
    /// </summary>
    Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(string repoUrl, string prNumber);

    Task<bool> DeleteBranchAsync(string repoUrl, string branchName);
    Task<bool> CreatePullRequestCommentAsync(string repoUrl, string prNumber, string body);
}
