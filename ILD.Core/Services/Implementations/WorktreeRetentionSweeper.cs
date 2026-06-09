using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Reclaims disk from old runs. Each run keeps its own worktree and branch so
/// finished runs stay inspectable (see ADR-0008); this sweeper deletes the
/// whole run — worktree, local branch, and the run row (nodes, edge traversals,
/// event logs cascade) — once it has been terminal longer than the configured
/// <see cref="AppSettingKeys.RunRetentionDays"/> window.
///
/// Safety rails:
/// <list type="bullet">
/// <item>Runs pinned with <c>Retain == true</c> are never touched.</item>
/// <item>The run a still-active work item points at (its current run) is kept
/// until the work item is <c>Done</c>, so a parked Failed run isn't yanked out
/// from under a human reviewing it.</item>
/// <item>A retention value of <c>0</c> disables reclamation entirely.</item>
/// <item>Remote branches/PRs are left intact — only local state is reclaimed.</item>
/// </list>
/// The window is read from settings on every pass, so changing it in the UI
/// takes effect on the next sweep without a restart.
/// </summary>
public sealed class WorktreeRetentionSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WorktreeRetentionSweeper> _log;

    public WorktreeRetentionSweeper(IServiceScopeFactory scopes, ILogger<WorktreeRetentionSweeper> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reclamation is never urgent (the window is days); wait one interval
        // before the first sweep so it doesn't contend with app startup.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worktree retention sweep failed");
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var settings = sp.GetRequiredService<ISchedulerSettingsService>();
        var runStore = sp.GetRequiredService<ILoopRunStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var repo = sp.GetRequiredService<IRepositoryManager>();

        var retentionDays = await settings.GetRunRetentionDaysAsync(ct);
        if (retentionDays <= 0) return; // reclamation disabled

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
        var candidates = await runStore.GetReclaimableRunsAsync(cutoff);
        if (candidates.Count == 0) return;

        var reclaimed = 0;
        foreach (var run in candidates)
        {
            if (ct.IsCancellationRequested) return;

            // Keep the run a still-active work item is pointing at; only reclaim
            // it once the work item is Done or a newer run has superseded it.
            var wi = await workItems.GetWorkItemAsync(run.WorkItemId);
            if (wi is not null && wi.Status != RemoteWorkItemStatus.Done)
            {
                var current = await runStore.GetCurrentByWorkItemAsync(run.WorkItemId);
                if (current?.Id == run.Id) continue;
            }

            await ReclaimRunAsync(run, repo);

            if (await runStore.DeleteAsync(run.Id))
                reclaimed++;
        }

        if (reclaimed > 0)
            _log.LogInformation("Worktree retention reclaimed {Count} runs terminal before {Cutoff:o}", reclaimed, cutoff);
    }

    private async Task ReclaimRunAsync(Data.Entities.LoopRun run, IRepositoryManager repo)
    {
        // Resolve the base repo before destroying the worktree — afterwards the
        // worktree path is gone and the branch can no longer be located through it.
        string? baseRepoPath = null;
        if (!string.IsNullOrEmpty(run.WorktreePath))
        {
            try { baseRepoPath = await repo.ResolveBaseRepoPathAsync(run.WorktreePath); }
            catch (Exception ex) { _log.LogDebug(ex, "Could not resolve base repo for run {RunId}", run.Id); }

            try { await repo.DestroyWorktreeAsync(run.WorktreePath); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to destroy worktree for run {RunId} at {Path}", run.Id, run.WorktreePath); }
        }

        if (baseRepoPath is not null && !string.IsNullOrEmpty(run.BranchName))
        {
            try { await repo.DeleteLocalBranchAsync(baseRepoPath, run.BranchName); }
            catch (Exception ex) { _log.LogDebug(ex, "Failed to delete branch {Branch} for run {RunId}", run.BranchName, run.Id); }
        }
    }
}
