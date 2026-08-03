using ILD.Data.Entities;
using Microsoft.Extensions.Configuration;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Where a repository's base clone lives on disk — the repo the Start node adds
/// per-run worktrees to. The convention has two parts (the repository's own
/// <c>WorktreesPath</c>, else <c>{App:DataPath}/repos/{id:N}</c>) and three
/// readers that must agree on it: the Start node, which clones there; the run
/// reclaimer, which deletes branches there; and the branch-name conflict check,
/// which reads branches there before a run exists.
/// </summary>
internal static class BaseRepoPath
{
    /// <summary>
    /// Where a repository with no usable <c>WorktreesPath</c> is cloned. Returns
    /// the path whether or not anything is there yet.
    /// </summary>
    public static string Fallback(Guid repositoryId, IConfiguration? config)
    {
        var dataPath = config?["App:DataPath"];
        return Path.GetFullPath(Path.Combine(
            string.IsNullOrWhiteSpace(dataPath) ? "data" : dataPath,
            "repos", repositoryId.ToString("N")));
    }

    /// <summary>
    /// The base clone that actually exists on disk for <paramref name="repo"/>,
    /// or null when it has not been cloned yet — which callers must read as
    /// "nothing local to inspect", not as an error.
    /// </summary>
    public static string? Existing(Repository repo, IConfiguration? config)
    {
        if (!string.IsNullOrWhiteSpace(repo.WorktreesPath)
            && Directory.Exists(Path.Combine(repo.WorktreesPath, ".git")))
            return repo.WorktreesPath;

        var fallback = Fallback(repo.Id, config);
        return Directory.Exists(Path.Combine(fallback, ".git")) ? fallback : null;
    }
}
