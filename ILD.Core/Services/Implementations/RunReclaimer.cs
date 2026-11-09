using ILD.Core.Services.Implementations.Files;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <inheritdoc cref="IRunReclaimer"/>
public sealed class RunReclaimer : IRunReclaimer
{
    private readonly IRepositoryManager _repo;
    private readonly IRunHost _host;
    private readonly IProviderStore _providers;
    private readonly IWorktreePreviewService _preview;
    private readonly IWorkItemNotifier _notifier;
    private readonly IConfiguration? _config;
    private readonly ILogger<RunReclaimer>? _log;

    public RunReclaimer(
        IRepositoryManager repo,
        IProviderStore providers,
        IRunHost host,
        IWorktreePreviewService? preview = null,
        IWorkItemNotifier? notifier = null,
        IConfiguration? config = null,
        ILogger<RunReclaimer>? log = null)
    {
        _repo = repo;
        _host = host;
        _providers = providers;
        _preview = preview ?? new NoopPreviewService();
        _notifier = notifier ?? new NoopWorkItemNotifier();
        _config = config;
        _log = log;
    }

    public async Task<bool> ReclaimLocalStateAsync(LoopRun run)
    {
        // Decided before anything reaches the run: stopping its preview could start a stopped container again.
        var keptPreviewData = await KeptPreviewDataOfAsync(run);
        await StopPreviewIfRunningAsync(run);

        // The ILD extension holds the API token and nothing else can find it once
        // the run row is gone, so failing to remove it keeps the run for a retry.
        // What the agent left in its own pi directories never holds the reclaim up.
        try
        {
            if (!await AgentRunFiles.DeleteAsync(run.Id))
                _log?.LogWarning("Could not fully clear the pi agent directories of run {RunId}", run.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Failed to remove the pi ILD extension of run {RunId}", run.Id);
            return false;
        }

        // Resolve the base repo before destroying the worktree — afterwards
        // the branch can no longer be located through it.
        string? baseRepoPath = null;
        // Not checked on this disk first: a run host that is not on it holds the run elsewhere,
        // and deleting what is not there is a no-op for every host.
        if (!string.IsNullOrEmpty(run.WorktreePath))
        {
            try { baseRepoPath = await _repo.ResolveBaseRepoPathAsync(run.WorktreePath); }
            catch (Exception ex) { _log?.LogDebug(ex, "Could not resolve base repo for run {RunId}", run.Id); }

            // The preview's ${STATE_DIR}, per-run volumes and npm cache included, belongs to the
            // worktree and goes before it, while a run that is running can still be reached.
            if (!await DeletePreviewStateAsync(run))
                return false;

            // The run's copy of its repository's kept preview data is put back in the store first, when the run ended cleanly.
            try { await _host.DeleteAsync(new RunEnvironment(run.Id, run.WorktreePath, keptPreviewData)); }
            catch (Exception ex) { _log?.LogWarning(ex, "Failed to destroy worktree for run {RunId} at {Path}", run.Id, run.WorktreePath); }

            // Something survived the destroy attempt: report failure so the caller
            // keeps the run row and a later sweep retries, instead of deleting the
            // row and stranding what is there as untracked disk.
            if (AgentWritableFiles.EntryExists(run.WorktreePath))
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

    /// <summary>
    /// A preview's processes and ports outlive the directory they were started
    /// in, and every preview endpoint is addressed by worktree path — so a
    /// worktree destroyed under a live preview leaves it running with no way
    /// left to reach its stop control. Same rule the Done transition applies
    /// (<see cref="WorkItemManager"/>): stop it while the user still could.
    /// Best-effort, like that one — a preview that will not stop must not
    /// strand the worktree and branch it is sitting on.
    /// </summary>
    private async Task StopPreviewIfRunningAsync(LoopRun run)
    {
        if (string.IsNullOrEmpty(run.WorktreePath) || !_preview.IsPreviewRunning(run.WorktreePath))
            return;

        try
        {
            await _preview.StopAsync(run.WorktreePath);
            await _notifier.PreviewStateChangedAsync(run.WorkItemId);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to stop the preview on {Path} before reclaiming run {RunId}",
                run.WorktreePath, run.Id);
        }
    }

    /// <summary>
    /// The repository whose store the run's copy of the kept preview data goes back to: only a run that is still running as it
    /// was made. A run that crashed, or outlived a restart of ILD, may have left its copy half-written, and the stored copy stays.
    /// </summary>
    private async Task<Guid?> KeptPreviewDataOfAsync(LoopRun run)
    {
        if (string.IsNullOrEmpty(run.WorktreePath) || run.RepositoryId is not { } repositoryId) return null;
        try
        {
            if (await _host.IsRunningAsync(new RunEnvironment(run.Id, run.WorktreePath)))
                return repositoryId;
            _log?.LogDebug("Run {RunId} is not running, so any preview data it kept is not stored", run.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "Could not tell whether run {RunId} is running, so the preview data it kept is not stored", run.Id);
        }
        return null;
    }

    private async Task<bool> DeletePreviewStateAsync(LoopRun run)
    {
        try
        {
            if (await _host.DeleteAsync([_host.PreviewStateDirectoryFor(run.WorktreePath!)]))
                return true;
            _log?.LogWarning("Could not remove the preview state directory of run {RunId}", run.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log?.LogWarning(ex, "Could not remove the preview state directory of run {RunId}", run.Id);
        }
        return false;
    }

    private async Task<string?> ResolveBaseRepoFromRepositoryAsync(Guid? repositoryId)
    {
        if (repositoryId is null) return null;
        Repository? repo;
        try { repo = await _providers.GetRepositoryByIdAsync(repositoryId.Value); }
        catch { return null; }
        if (repo is null) return null;
        return BaseRepoPath.Existing(repo, _config);
    }
}
