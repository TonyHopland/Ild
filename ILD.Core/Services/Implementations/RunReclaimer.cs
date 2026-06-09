using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <inheritdoc cref="IRunReclaimer"/>
public sealed class RunReclaimer : IRunReclaimer
{
    private readonly IRepositoryManager _repo;
    private readonly IProviderStore _providers;
    private readonly IConfiguration? _config;
    private readonly ILogger<RunReclaimer>? _log;

    public RunReclaimer(
        IRepositoryManager repo,
        IProviderStore providers,
        IConfiguration? config = null,
        ILogger<RunReclaimer>? log = null)
    {
        _repo = repo;
        _providers = providers;
        _config = config;
        _log = log;
    }

    public async Task<bool> ReclaimLocalStateAsync(LoopRun run)
    {
        // Resolve the base repo before destroying the worktree — afterwards
        // the branch can no longer be located through it.
        string? baseRepoPath = null;
        if (!string.IsNullOrEmpty(run.WorktreePath) && Directory.Exists(run.WorktreePath))
        {
            try { baseRepoPath = await _repo.ResolveBaseRepoPathAsync(run.WorktreePath); }
            catch (Exception ex) { _log?.LogDebug(ex, "Could not resolve base repo for run {RunId}", run.Id); }

            try { await _repo.DestroyWorktreeAsync(run.WorktreePath); }
            catch (Exception ex) { _log?.LogWarning(ex, "Failed to destroy worktree for run {RunId} at {Path}", run.Id, run.WorktreePath); }

            // The worktree survived the destroy attempt: report failure so the
            // caller keeps the run row and a later sweep retries, instead of
            // deleting the row and stranding the directory as untracked disk.
            if (Directory.Exists(run.WorktreePath))
                return false;
        }

        if (string.IsNullOrEmpty(run.BranchName))
            return true;

        // The worktree may already be gone (explicit cleanup, manual deletion);
        // fall back to locating the base repo through the run's repository.
        baseRepoPath ??= await ResolveBaseRepoFromRepositoryAsync(run.RepositoryId);
        if (baseRepoPath is null)
        {
            // No way to locate the base repo (repository row gone and no
            // worktree to walk up from). The branch is unreachable to us;
            // don't hold the run row hostage over it.
            _log?.LogWarning("Cannot resolve base repo for run {RunId}; skipping deletion of branch '{Branch}'",
                run.Id, run.BranchName);
            return true;
        }

        // A worktree registration whose directory disappeared pins its branch
        // ("checked out at ..."); prune first so branch -D can succeed.
        try { await _repo.PruneWorktreesAsync(baseRepoPath); }
        catch (Exception ex) { _log?.LogDebug(ex, "worktree prune failed in {Repo}", baseRepoPath); }

        try
        {
            if (!await _repo.LocalBranchExistsAsync(baseRepoPath, run.BranchName))
                return true;
            if (await _repo.DeleteLocalBranchAsync(baseRepoPath, run.BranchName))
                return true;
            _log?.LogWarning("Failed to delete branch {Branch} for run {RunId} in {Repo}",
                run.BranchName, run.Id, baseRepoPath);
            return false;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to delete branch {Branch} for run {RunId}", run.BranchName, run.Id);
            return false;
        }
    }

    private async Task<string?> ResolveBaseRepoFromRepositoryAsync(Guid? repositoryId)
    {
        if (repositoryId is null) return null;
        Repository? repo;
        try { repo = await _providers.GetRepositoryByIdAsync(repositoryId.Value); }
        catch { return null; }
        if (repo is null) return null;

        if (!string.IsNullOrWhiteSpace(repo.WorktreesPath)
            && Directory.Exists(Path.Combine(repo.WorktreesPath, ".git")))
            return repo.WorktreesPath;

        // Same fallback location StartNodeExecutor clones into.
        var dataPath = _config?["App:DataPath"];
        var fallback = Path.GetFullPath(Path.Combine(
            string.IsNullOrWhiteSpace(dataPath) ? "data" : dataPath,
            "repos", repositoryId.Value.ToString("N")));
        return Directory.Exists(Path.Combine(fallback, ".git")) ? fallback : null;
    }
}
