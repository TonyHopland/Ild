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
    Task<bool> EnablePullRequestAutoMergeAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    Task RegisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl);
    Task UnregisterWebhookAsync(HttpClient http, ResolvedRemoteRepository repo, string callbackUrl);
    Task<RemotePrStatus> GetPullRequestStatusAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);
    /// <summary>
    /// A window onto one failing check's log, fetched with the provider
    /// credentials the agent does not hold. <paramref name="checkId"/> is the
    /// handle carried on <see cref="RemotePrCheck.CheckId"/>;
    /// <paramref name="tailLines"/> counts back from the end (where the error
    /// is) and <paramref name="offset"/> skips that many lines from the end, so
    /// an agent can walk backwards. Providers whose CI lives outside the forge
    /// return <see cref="RemoteCiLog.Unavailable"/> rather than throwing.
    /// </summary>
    Task<RemoteCiLog> GetCheckLogAsync(HttpClient http, ResolvedRemoteRepository repo, string checkId, int tailLines, int offset);

    /// <summary>
    /// The whole review ledger for a pull request: every inline comment with the
    /// commit it was written against and its thread's state, every finding a
    /// review body carries without surfacing it as a thread, and every review's
    /// verdict. A forge that cannot be read answers with
    /// <see cref="RemotePrReviewLedger.Unavailable"/>.
    /// </summary>
    Task<RemotePrReviewLedger> GetPullRequestReviewLedgerAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber);

    /// <summary>Answer one review comment on its own thread, with the provider credentials.</summary>
    Task<RemotePrWriteResult> ReplyToReviewThreadAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber, string commentId, string body);

    /// <summary>Whether this provider can resolve a review thread at all.</summary>
    bool SupportsThreadResolution { get; }

    /// <summary>
    /// Mark a review thread resolved. Providers whose API has no thread concept
    /// say so rather than claiming success — the same degradation
    /// <see cref="GetCheckLogAsync"/> uses.
    /// </summary>
    Task<RemotePrWriteResult> ResolveReviewThreadAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber, string threadId);

    Task<bool> DeleteBranchAsync(HttpClient http, ResolvedRemoteRepository repo, string branchName);
    Task<RemotePrWriteResult> CreatePullRequestCommentAsync(HttpClient http, ResolvedRemoteRepository repo, string prNumber, string body);
    bool VerifyWebhookSignature(string body, IReadOnlyDictionary<string, string> headers, string secret);
    WebhookPayload? ParseWebhookPayload(string body, IReadOnlyDictionary<string, string> headers);
}