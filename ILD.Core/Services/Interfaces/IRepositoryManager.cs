using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public sealed record GitAuthOptions(string RemoteUrl, string? ApiKey, string? ProviderType = null);

/// <summary>
/// Outcome of a <see cref="IRepositoryManager.MergeAsync"/>. On a clean merge
/// <see cref="Success"/> is true and the lists are empty. On a conflict it is
/// false and <see cref="ConflictedFiles"/> names the unmerged paths; the markers
/// are left in the worktree (call <see cref="IRepositoryManager.AbortMergeAsync"/>
/// to discard them). A non-zero exit with no conflicted files (e.g. an unknown
/// ref) is reported via <see cref="Error"/>.
/// </summary>
public sealed record GitMergeResult(bool Success, IReadOnlyList<string> ConflictedFiles, string? Error = null);

public interface IRepositoryManager
{
    /// <summary>
    /// Clone <paramref name="cloneUrl"/> into <paramref name="targetPath"/>.
    /// Returns false on failure (caller decides whether to abort the run).
    /// </summary>
    Task<(bool Success, string? Error)> CloneAsync(string cloneUrl, string targetPath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);

    Task<string> CreateWorktreeAsync(string repoPath, string branchName);

    /// <summary>
    /// Create a worktree on a fresh branch <paramref name="branchName"/> based at
    /// <paramref name="baseRef"/> (e.g. <c>origin/main</c>), without disturbing the
    /// base repo's own checked-out branch. Any pre-existing worktree/branch at the
    /// target is torn down first so the result is a clean composition surface —
    /// used to build throwaway integration worktrees for combined previews.
    /// </summary>
    Task<string> CreateWorktreeFromAsync(string repoPath, string branchName, string baseRef);

    /// <summary>
    /// Merge <paramref name="branchRef"/> into the worktree's current branch as a
    /// merge commit with <paramref name="commitMessage"/>. See <see cref="GitMergeResult"/>
    /// for the conflict contract.
    /// </summary>
    Task<GitMergeResult> MergeAsync(string worktreePath, string branchRef, string commitMessage, CancellationToken cancellationToken = default);

    /// <summary>Abort an in-progress merge, restoring the worktree to its pre-merge state.</summary>
    Task AbortMergeAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The worktree's unmerged (conflicted) paths — empty when there is no
    /// conflict left to resolve. Lets a caller tell a human-resolved worktree
    /// from one that still has markers.
    /// </summary>
    Task<IReadOnlyList<string>> GetUnmergedFilesAsync(string worktreePath, CancellationToken cancellationToken = default);

    /// <summary>True when a merge is in progress (<c>MERGE_HEAD</c> exists) but not yet committed.</summary>
    Task<bool> IsMergeInProgressAsync(string worktreePath, CancellationToken cancellationToken = default);

    Task DestroyWorktreeAsync(string worktreePath);
    Task<bool> ValidateWorktreeHealthAsync(string worktreePath);
    Task<bool> CheckoutBranchAsync(string worktreePath, string branchName);
    Task<bool> FetchAsync(string worktreePath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);
    Task<bool> RebaseAsync(string worktreePath, string upstreamBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-reset the working tree to <paramref name="revision"/> (e.g. <c>origin/main</c>).
    /// Useful for syncing a base repo without merging — works regardless of merge history.
    /// </summary>
    Task<bool> ResetHardAsync(string worktreePath, string revision, CancellationToken cancellationToken = default);
    Task<bool> CommitAsync(string worktreePath, string message);
    Task<(bool Success, string? Error)> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);
    Task<string?> GetDiffAsync(string worktreePath);
    Task<int> GetCommitsAheadCountAsync(string worktreePath, string targetBranch);
    Task<string?> ReadFileAsync(string worktreePath, string relativePath);

    /// <summary>
    /// List every file in the worktree (tracked and untracked, ignoring
    /// <c>.gitignore</c>d paths), each tagged with its change status relative
    /// to the default branch's fork point. Files deleted on the branch are
    /// included so a PR-style diff view can still surface them. Returns an
    /// empty list if <paramref name="worktreePath"/> is not a valid worktree.
    /// </summary>
    Task<IReadOnlyList<WorktreeFileEntry>> ListWorktreeFilesAsync(string worktreePath);

    /// <summary>
    /// Read a single worktree file's full content together with its unified
    /// diff against the default branch's fork point. Content is null for binary
    /// or missing files; the diff is null when the file is unchanged. Returns
    /// null if the path escapes the worktree.
    /// </summary>
    Task<WorktreeFileContentResponse?> ReadWorktreeFileAsync(string worktreePath, string relativePath);

    /// <summary>
    /// Delete a local branch from the repository at <paramref name="repoPath"/>.
    /// </summary>
    Task<bool> DeleteLocalBranchAsync(string repoPath, string branchName);

    /// <summary>
    /// True if <paramref name="branchName"/> exists as a local branch in the
    /// repository at <paramref name="repoPath"/>.
    /// </summary>
    Task<bool> LocalBranchExistsAsync(string repoPath, string branchName);

    /// <summary>
    /// Run <c>git worktree prune</c> in the repository at <paramref name="repoPath"/>.
    /// Clears stale worktree registrations whose directories no longer exist —
    /// a stale registration pins its branch and blocks <c>git branch -D</c>.
    /// </summary>
    Task PruneWorktreesAsync(string repoPath);

    /// <summary>
    /// Resolve the base (main) repository path from a worktree path.
    /// Returns null if the worktree is not a valid git worktree.
    /// </summary>
    Task<string?> ResolveBaseRepoPathAsync(string worktreePath);
}
