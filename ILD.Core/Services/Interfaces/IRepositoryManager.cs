using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public sealed record GitAuthOptions(string RemoteUrl, string? ApiKey, string? ProviderType = null);

/// <summary>
/// Details inferred from a remote when adding a repository: the default branch
/// from the remote's advertised <c>HEAD</c> symref and a name derived from the
/// clone URL. Either field is null when it can't be determined.
/// </summary>
public sealed record RemoteRepositoryInfo(string? DefaultBranch, string? Name);

/// <summary>
/// Outcome of <see cref="IRepositoryManager.RebaseAsync"/>. On failure
/// <paramref name="ConflictedFiles"/> lists the paths git could not merge — empty
/// when the rebase was refused before it started (e.g. untracked files in the way),
/// in which case <paramref name="Error"/> carries git's own explanation.
/// </summary>
public sealed record RebaseResult(bool Success, IReadOnlyList<string> ConflictedFiles, string? Error);

public interface IRepositoryManager
{
    /// <summary>
    /// Clone <paramref name="cloneUrl"/> into <paramref name="targetPath"/>.
    /// Returns false on failure (caller decides whether to abort the run).
    /// </summary>
    Task<(bool Success, string? Error)> CloneAsync(string cloneUrl, string targetPath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);

    Task<string> CreateWorktreeAsync(string repoPath, string branchName);
    Task DestroyWorktreeAsync(string worktreePath);
    Task<bool> ValidateWorktreeHealthAsync(string worktreePath);
    Task<bool> CheckoutBranchAsync(string worktreePath, string branchName);

    /// <summary>
    /// Bring <c>refs/remotes/origin/*</c> in line with the remote: every branch,
    /// not just the checked-out one, and refs the remote no longer has pruned.
    /// Local branches are untouched.
    /// </summary>
    Task<bool> FetchAsync(string worktreePath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);

    /// <summary>
    /// Rebase the branch checked out in <paramref name="worktreePath"/> onto
    /// <paramref name="upstreamBranch"/>. A rebase that cannot complete is
    /// <em>aborted</em> before returning rather than left in place: a half-rebased
    /// worktree has no usable HEAD and would break every later node. The conflicted
    /// paths are captured first and returned to the caller.
    ///
    /// <para>
    /// The abort is <c>git rebase --abort</c> and therefore best-effort — git can
    /// itself fail to unwind (a stale <c>index.lock</c>, an unreadable object) — so
    /// this closes every path ILD controls, not the residual risk that git's own
    /// cleanup fails.
    /// </para>
    ///
    /// <para>Rebases local refs only — never contacts the remote, so it needs no auth.</para>
    /// </summary>
    Task<RebaseResult> RebaseAsync(string worktreePath, string upstreamBranch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-reset the working tree to <paramref name="revision"/> (e.g. <c>origin/main</c>).
    /// Useful for syncing a base repo without merging — works regardless of merge history.
    /// </summary>
    Task<bool> ResetHardAsync(string worktreePath, string revision, CancellationToken cancellationToken = default);
    Task<bool> CommitAsync(string worktreePath, string message);
    Task<(bool Success, string? Error)> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);
    Task<string?> GetDiffAsync(string worktreePath);

    /// <summary>
    /// The file list behind <see cref="GetDiffAsync"/>: tracked paths that differ
    /// from <c>HEAD</c>, staged or not. Untracked files are deliberately excluded —
    /// a rebase leaves them alone, and treating repository build output as "dirty"
    /// would block the operation for no gain.
    /// </summary>
    Task<IReadOnlyList<string>> GetUncommittedFilesAsync(string worktreePath);

    Task<int> GetCommitsAheadCountAsync(string worktreePath, string targetBranch);

    /// <summary>
    /// How many commits <paramref name="upstreamRef"/> holds that <c>HEAD</c> does
    /// not — i.e. what a rebase onto it would replay. Zero means <c>HEAD</c> already
    /// contains everything the ref does.
    /// </summary>
    Task<int> GetCommitsBehindCountAsync(string worktreePath, string upstreamRef);
    Task<string?> ReadFileAsync(string worktreePath, string relativePath);

    /// <summary>
    /// List every file in the worktree (tracked and untracked, ignoring
    /// <c>.gitignore</c>d paths), each tagged with its change status relative
    /// to the default branch's fork point. Files deleted on the branch are
    /// included so a PR-style diff view can still surface them. Returns an
    /// empty list if <paramref name="worktreePath"/> is not a valid worktree.
    /// The diff is anchored on <paramref name="defaultBranch"/> (the repository's
    /// stored default branch) when supplied, falling back to <c>origin/HEAD</c>.
    /// </summary>
    Task<IReadOnlyList<WorktreeFileEntry>> ListWorktreeFilesAsync(string worktreePath, string? defaultBranch = null);

    /// <summary>
    /// Read a single worktree file's full content together with its unified
    /// diff against the default branch's fork point. Content is null for binary
    /// or missing files, except that a binary image small enough to inline also
    /// carries its bytes as base64 plus a media type so the file viewer can
    /// render it; the diff is null when the file is unchanged. Returns
    /// null if the path escapes the worktree. The diff is anchored on
    /// <paramref name="defaultBranch"/> (the repository's stored default branch)
    /// when supplied, falling back to <c>origin/HEAD</c>.
    /// </summary>
    Task<WorktreeFileContentResponse?> ReadWorktreeFileAsync(string worktreePath, string relativePath, string? defaultBranch = null);

    /// <summary>
    /// Inspect a remote without cloning to infer the default branch (from the
    /// remote's advertised <c>HEAD</c> symref) and a name (from the clone URL).
    /// Honors the same <paramref name="auth"/> as clone. Returns null when the
    /// remote can't be reached (private without creds, offline, local path).
    /// </summary>
    Task<RemoteRepositoryInfo?> InspectRemoteAsync(string cloneUrl, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);

    /// <summary>
    /// Ask the remote at <paramref name="cloneUrl"/> directly whether it already
    /// publishes <paramref name="branchName"/>. Unlike
    /// <see cref="RemoteBranchExistsAsync"/> this needs no local clone and no
    /// prior fetch, so it can be asked before a run — and its worktree — exists.
    /// </summary>
    /// <returns>
    /// Null when the remote could not be reached. Callers must not read that as
    /// "absent": it means the question went unanswered.
    /// </returns>
    Task<bool?> RemoteHasBranchAsync(string cloneUrl, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);

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
    /// True if <paramref name="branchName"/> exists as an <c>origin</c> remote-tracking
    /// branch in the repository at <paramref name="repoPath"/>. Reflects the last
    /// <see cref="FetchAsync"/>, so fetch first when the answer must be current.
    /// </summary>
    Task<bool> RemoteBranchExistsAsync(string repoPath, string branchName);

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
