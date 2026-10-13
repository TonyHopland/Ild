using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Brings a repository's base clone (<see cref="BaseRepoPath"/>) up to date with
/// its remote: fetched where it is, or cloned where runs would clone it.
/// </summary>
internal static class BaseRepoClone
{
    private const int DeleteAttempts = 3;

    private static readonly SharedEntries<Guid, SemaphoreSlim> Locks = new(() => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The repository's lock, released by disposing it. A run's Start node holds
    /// it while it clones or fetches the base clone, resets it, adds the run's
    /// worktree and fetches in that worktree, which shares the base clone's refs;
    /// a chat holds it while it clones or fetches the base clone. Without it a
    /// chat finding no base clone and a Start node cloning one would clone into
    /// the same folder, and the loser's cleanup would delete the winner's clone,
    /// and their fetches would contend for the same remote-tracking refs. Other
    /// writers of the base clone do not take it: the Start node's rebase of the
    /// run's own branch, the PR node's push and fetch, pull_branch, and the run
    /// reclaimer.
    /// </summary>
    public static async Task<IDisposable> LockAsync(Guid repositoryId, CancellationToken ct)
    {
        var gate = Locks.Enter(repositoryId);
        try
        {
            await gate.WaitAsync(ct);
        }
        catch
        {
            Locks.Leave(repositoryId);
            throw;
        }
        return new Held(repositoryId, gate);
    }

    /// <summary>Whether anyone holds or waits for the repository's lock.</summary>
    internal static bool IsTracked(Guid repositoryId) => Locks.Contains(repositoryId);

    /// <summary>
    /// Fetch the existing base clone, or make it. Call under <see cref="LockAsync"/>.
    /// A fresh clone that fails or is cancelled takes the folder it created with
    /// it: the base clone is found by its <c>.git</c> folder, so a partial one
    /// would be taken for a clone by every chat and every run's Start node after
    /// it instead of being cloned again. A partial clone that cannot be removed is
    /// logged, and named in the error of a failed clone.
    /// </summary>
    public static async Task<(bool Ok, string? Path, string? Error)> EnsureAsync(
        IRepositoryManager git, Repository repo, IConfiguration? config, GitAuthOptions? auth, CancellationToken ct,
        ILogger? log = null)
    {
        if (BaseRepoPath.Existing(repo, config) is { } existing)
        {
            return await git.FetchAsync(existing, ct, auth)
                ? (true, existing, null)
                : (false, null, "git fetch origin failed");
        }

        var path = BaseRepoPath.Fallback(repo.Id, config);
        var created = !Directory.Exists(path);
        (bool Success, string? Error) clone;
        try
        {
            clone = await git.CloneAsync(repo.CloneUrl, path, ct, auth);
        }
        catch (Exception) when (created)
        {
            await DeletePartialCloneAsync(path, log);
            throw;
        }

        if (clone.Success)
            return (true, path, null);

        var error = $"git clone failed: {clone.Error?.Trim()}";
        if (created && !await DeletePartialCloneAsync(path, log))
            error += $"; the partial clone at '{path}' could not be removed";
        return (false, null, error);
    }

    // The repos folder is shared with the agent uid (ADR-0014), so the partial
    // clone is deleted as the agent, never followed through a link it planted.
    // A killed git may still be writing as it dies, hence the second attempt.
    private static async Task<bool> DeletePartialCloneAsync(string path, ILogger? log)
    {
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            if (await AgentWritableFiles.DeleteAsync([path], CancellationToken.None))
                return true;
        }
        log?.LogError("Could not remove the partial base clone at {Path}; remove it by hand, or it is taken for a clone", path);
        return false;
    }

    private sealed class Held(Guid repositoryId, SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            gate.Release();
            Locks.Leave(repositoryId);
        }
    }
}
