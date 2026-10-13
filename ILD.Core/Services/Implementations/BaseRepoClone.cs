using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.Extensions.Configuration;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Brings a repository's base clone (<see cref="BaseRepoPath"/>) up to date with
/// its remote: fetched where it is, or cloned where runs would clone it.
/// </summary>
internal static class BaseRepoClone
{
    /// <summary>
    /// Fetch the existing base clone, or make it. A fresh clone that fails or is
    /// cancelled takes the folder it created with it: the base clone is found by
    /// its <c>.git</c> folder, so a partial one would be fetched, and fail, for
    /// every chat and every run's Start node after it instead of being cloned again.
    /// </summary>
    public static async Task<(bool Ok, string? Path, string? Error)> EnsureAsync(
        IRepositoryManager git, Repository repo, IConfiguration? config, GitAuthOptions? auth, CancellationToken ct)
    {
        if (BaseRepoPath.Existing(repo, config) is { } existing)
        {
            return await git.FetchAsync(existing, ct, auth)
                ? (true, existing, null)
                : (false, null, "git fetch origin failed");
        }

        var path = BaseRepoPath.Fallback(repo.Id, config);
        var created = !Directory.Exists(path);
        var cloned = false;
        try
        {
            var (success, error) = await git.CloneAsync(repo.CloneUrl, path, ct, auth);
            cloned = success;
            return success ? (true, path, null) : (false, null, $"git clone failed: {error?.Trim()}");
        }
        finally
        {
            if (!cloned && created && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
