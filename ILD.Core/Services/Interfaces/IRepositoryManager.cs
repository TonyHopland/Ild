using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public sealed record GitAuthOptions(string RemoteUrl, string? ApiKey, string? ProviderType = null);

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
    Task<bool> FetchAsync(string worktreePath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);
    Task<bool> PullAsync(string worktreePath, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);
    Task<bool> RebaseAsync(string worktreePath, string upstreamBranch, CancellationToken cancellationToken = default);
    Task<bool> CommitAsync(string worktreePath, string message);
    Task<(bool Success, string? Error)> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default, GitAuthOptions? auth = null);
    Task<string?> GetDiffAsync(string worktreePath);
    Task<int> GetCommitsAheadCountAsync(string worktreePath, string targetBranch);
    Task<string?> ReadFileAsync(string worktreePath, string relativePath);
}
