using System.Net.Http;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

public interface IRemoteGitProviderAdapter
{
    string ProviderType { get; }
    string WebhookRouteSegment { get; }
    ResolvedRemoteRepository? TryResolve(RemoteProvider provider, Uri repoUri);
    Task<RemotePrResult> CreatePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string sourceBranch, string targetBranch, string title, string body);
    Task<bool> MergePullRequestAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl);
    Task UnregisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl);
    Task<RemotePrStatus> GetPullRequestStatusAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName);
    Task<bool> CreatePullRequestCommentAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber, string body);
    bool VerifyWebhookSignature(string body, IReadOnlyDictionary<string, string> headers, string secret);
    WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers);
}