using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IRepositoryManager
{
    Task<string> CreateWorktreeAsync(string repoPath, string branchName);
    Task DestroyWorktreeAsync(string worktreePath);
    Task<bool> ValidateWorktreeHealthAsync(string worktreePath);
    Task<bool> CheckoutBranchAsync(string worktreePath, string branchName);
    Task<bool> FetchAsync(string worktreePath, CancellationToken cancellationToken = default);
    Task<bool> PullAsync(string worktreePath, CancellationToken cancellationToken = default);
    Task<bool> RebaseAsync(string worktreePath, string upstreamBranch, CancellationToken cancellationToken = default);
    Task<bool> CommitAsync(string worktreePath, string message);
    Task<(bool Success, string? Error)> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken = default);
    Task<string?> GetDiffAsync(string worktreePath);
    Task<int> GetCommitsAheadCountAsync(string worktreePath, string targetBranch);
    Task<string?> ReadFileAsync(string worktreePath, string relativePath);
}
