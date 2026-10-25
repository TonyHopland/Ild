using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The forge refused a merge because the pull request's head is no longer the commit the
/// caller asked to merge. Not a failed merge: nothing was merged and the caller may look
/// again against the new head.
/// </summary>
public sealed class RemoteHeadMovedException(string expected, string? current)
    : Exception($"the pull request head moved on from {expected}")
{
    public string Expected { get; } = expected;
    public string? Current { get; } = current;
}

public interface IRemoteProvider
{
    Task<RemotePrResult> CreatePullRequestAsync(string repoUrl, string sourceBranch, string targetBranch, string title, string body);
    /// <summary>
    /// Merge the pull request. With <paramref name="expectedHeadSha"/> the forge must refuse if the head
    /// is a different commit, reported as <see cref="RemoteHeadMovedException"/> rather than false.
    /// </summary>
    Task<bool> MergePullRequestAsync(string repoUrl, string prNumber, string? expectedHeadSha = null);

    /// <summary>
    /// Turn on auto-merge for a pull request when the repository supports it.
    /// Best-effort: returns <c>false</c> (never throws) when no provider matches
    /// or the repository/provider does not support auto-merge.
    /// </summary>
    Task<bool> EnablePullRequestAutoMergeAsync(string repoUrl, string prNumber);
    Task RegisterWebhookAsync(string repoUrl, string callbackUrl);
    Task UnregisterWebhookAsync(string repoUrl, string callbackUrl);
    Task<RemotePrStatus> GetPullRequestStatusAsync(string repoUrl, string prNumber);

    /// <summary>
    /// Fetch a full PR snapshot (title, description, mergeability, CI verdict,
    /// review decision, conversation). Returns null when the PR cannot be
    /// resolved or fetched. Backs the PR heartbeat poller and the feedback UI.
    /// </summary>
    Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(string repoUrl, string prNumber);

    /// <summary>
    /// A window onto one failing check's log (see
    /// <see cref="IRemoteGitProviderAdapter.GetCheckLogAsync"/>). Never throws:
    /// an unresolvable repository or a provider without fetchable logs comes
    /// back as <see cref="RemoteCiLog.Unavailable"/>.
    /// </summary>
    Task<RemoteCiLog> GetCheckLogAsync(string repoUrl, string checkId, int tailLines, int offset);

    /// <summary>
    /// Everything said on a pull request's review — inline comments with their
    /// thread state, the findings a review body suppressed, and each review's
    /// verdict. Never throws: an unresolvable repository or a forge that will
    /// not answer comes back as
    /// <see cref="RemotePrReviewLedger.Unavailable"/>.
    /// </summary>
    Task<RemotePrReviewLedger> GetPullRequestReviewLedgerAsync(string repoUrl, string prNumber);

    /// <summary>
    /// Answer one review comment on its own thread. Never throws; a refusal
    /// comes back as <see cref="RemotePrWriteResult.Ok"/> false with a message.
    /// </summary>
    Task<RemotePrWriteResult> ReplyToReviewThreadAsync(string repoUrl, string prNumber, string commentId, string body);

    /// <summary>
    /// Mark a review thread resolved. Providers whose API has no such concept
    /// answer with a message rather than claiming success.
    /// </summary>
    Task<RemotePrWriteResult> ResolveReviewThreadAsync(string repoUrl, string prNumber, string threadId);

    /// <summary>
    /// Whether this repository's provider can resolve a review thread at all.
    /// Asked before an agent's resolve is queued, so a provider that can never
    /// do it says so then rather than answering "queued" for something that will
    /// never happen. False for a repository no provider matches.
    /// </summary>
    Task<bool> SupportsThreadResolutionAsync(string repoUrl);

    Task<bool> DeleteBranchAsync(string repoUrl, string branchName);

    /// <summary>
    /// Post a comment on the pull request itself. <see cref="RemotePrWriteResult.Ok"/>
    /// carries the meaning the old <c>bool</c> had — the PR node fails only when
    /// it is false — while the id is best-effort, so a post the provider accepted
    /// but did not name records nothing and fails nothing.
    /// </summary>
    Task<RemotePrWriteResult> CreatePullRequestCommentAsync(string repoUrl, string prNumber, string body);
}
