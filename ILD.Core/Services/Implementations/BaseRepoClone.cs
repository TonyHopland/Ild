using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    /// <summary>
    /// The lock every clone, fetch and reset of a repository's base clone made by
    /// a run's Start node or for a chat is taken under, released by disposing it.
    /// Without it a chat finding no base clone and a Start node cloning one would
    /// clone into the same folder, and the loser's cleanup would delete the
    /// winner's clone.
    /// </summary>
    public static async Task<IDisposable> LockAsync(Guid repositoryId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Held(gate);
    }

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

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
