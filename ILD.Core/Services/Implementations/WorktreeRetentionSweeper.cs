using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Reclaims disk from finished runs. Each run keeps its own worktree and branch
/// so finished runs stay inspectable (see ADR-0008); this sweeper deletes the
/// whole run — worktree, local branch, and the run row (nodes, edge traversals,
/// event logs cascade) — once its work item is <c>Done</c> and the run finished
/// more than <see cref="AppSettingKeys.RunRetentionDays"/> days ago. With the
/// default of <c>0</c> that is the first pass after the work item is Done.
///
/// Safety rails:
/// <list type="bullet">
/// <item>Runs pinned with <c>Retain == true</c> are never touched.</item>
/// <item>No run of a work item that is not <c>Done</c> is reclaimed — neither
/// its current run nor one a newer run superseded — so a parked Failed run isn't
/// yanked out from under a human reviewing it. Runs of a work item the server no
/// longer has are reclaimed.</item>
/// <item>Remote branches/PRs are left intact — only local state is reclaimed.</item>
/// </list>
/// A pass runs every 6 hours, the first 10 minutes after start, and pages
/// through every candidate: the runs it keeps are the oldest, and would
/// otherwise fill one batch forever. Each work item's status is looked up once
/// per pass. The window is read from settings on every
/// pass, so changing it in the UI takes effect on the next sweep without a restart.
/// </summary>
public sealed class WorktreeRetentionSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    // Short first delay: a 6h wait before the first pass means a deployment
    // that restarts more often than every 6h never reclaims anything.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);
    private const int PageSize = 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WorktreeRetentionSweeper> _log;
    private readonly WorkItemActor _actor;

    public WorktreeRetentionSweeper(IServiceScopeFactory scopes, BootstrapCredentials instance, ILogger<WorktreeRetentionSweeper> log)
    {
        _scopes = scopes;
        _actor = WorkItemActor.System(instance.Username, "run retention");
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reclamation is never urgent (the window is days); wait before the
        // first sweep so it doesn't contend with app startup.
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = SweepInterval;

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

        var retentionDays = await settings.GetRunRetentionDaysAsync(ct);
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);

        // Kept candidates stay in the result set and reclaimed ones drop out,
        // so skipping the kept ones reaches the next unseen candidate.
        var reclaimed = 0;
        var kept = 0;
        // Whether each work item is still in play, looked up once per pass: kept
        // runs pile up under items that are not Done, and every lookup loads all
        // of the item's runs.
        var itemInPlay = new Dictionary<string, bool>(StringComparer.Ordinal);
        while (true)
        {
            var page = await runStore.GetReclaimableRunsAsync(cutoff, kept, PageSize);
            foreach (var candidate in page)
            {
                if (ct.IsCancellationRequested) return;
                if (await TryReclaimAsync(candidate.Id, cutoff, itemInPlay, ct))
                    reclaimed++;
                else
                    kept++;
            }
            if (page.Count < PageSize) break;
        }

        if (reclaimed > 0)
            _log.LogInformation("Worktree retention reclaimed {Count} runs terminal before {Cutoff:o}", reclaimed, cutoff);
    }

    private async Task<bool> TryReclaimAsync(
        Guid runId, DateTime cutoff, Dictionary<string, bool> itemInPlay, CancellationToken ct)
    {
        // A scope of its own: looking up a work item tracks all its runs, and a
        // re-read in a shared scope would return those copies instead of the
        // database, missing a pin or a retry made since.
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var runStore = sp.GetRequiredService<ILoopRunStore>();
        var workItems = sp.GetRequiredService<IWorkItemManager>();
        var reclaimer = sp.GetRequiredService<IRunReclaimer>();

        // Re-read the run just before touching it: between the candidate
        // query and now the user may have pinned it or retried it back to
        // Running — destroying an active run's worktree out from under it
        // would strand the resumed run.
        var run = await runStore.GetByIdAsync(runId);
        if (run is null || run.Retain) return false;
        if (run.Status is not (LoopRunStatus.Completed or LoopRunStatus.Failed or LoopRunStatus.Cancelled)) return false;
        if (run.CompletedAt is null || run.CompletedAt >= cutoff) return false;

        // Keep every run of a work item that is still in play, superseded or
        // not; only reclaim once the work item is Done.
        if (!itemInPlay.TryGetValue(run.WorkItemId, out var inPlay))
        {
            var wi = await workItems.GetWorkItemAsync(run.WorkItemId);
            inPlay = wi is not null && wi.Status != RemoteWorkItemStatus.Done;
            itemInPlay[run.WorkItemId] = inPlay;
        }
        if (inPlay) return false;

        // Only drop the run row once its local git state is verified gone;
        // otherwise keep the row so the next sweep can retry — a deleted
        // row makes the leftover worktree/branch permanently invisible.
        if (!await reclaimer.ReclaimLocalStateAsync(run))
        {
            _log.LogWarning("Run {RunId} reclaim incomplete; keeping run row for retry next sweep", run.Id);
            return false;
        }

        try
        {
            await workItems.WithdrawPendingProposalsOfRunAsync(_actor, run.Id, ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Run {RunId}'s pending edit proposals could not be withdrawn; keeping run row for retry next sweep", run.Id);
            return false;
        }

        return await runStore.DeleteAsync(run.Id);
    }
}
