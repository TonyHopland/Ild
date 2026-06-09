using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Runs once at startup to reconcile local loop-run state with the remote
/// WorkItem server. For every local LoopRun in Running or WaitingHuman
/// status the reconciler queries the server for the work item's current
/// state and either resumes execution via the recovery manager (server says
/// Running), re-adds the item to the active tracker without resuming
/// (server says HumanFeedback/WaitingForIld — re-tracking keeps the
/// heartbeat alive so the stale reclaimer can't hand the item to a second
/// concurrent run after a human resumes it), or cancels the local run
/// (item gone, Done, or reclaimed by the server) so an orphaned Running
/// row can't be resurrected by a later restart and fight the fresh run.
///
/// Registered as a IHostedService so it runs before the poller's first
/// tick but is guaranteed to complete within the host startup timeout.
/// </summary>
public sealed class RemoteWorkItemStartupReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<WorkItemSchedulerOptions> _options;
    private readonly ILogger<RemoteWorkItemStartupReconciler> _log;

    public RemoteWorkItemStartupReconciler(
        IServiceScopeFactory scopes,
        IOptionsMonitor<WorkItemSchedulerOptions> options,
        ILogger<RemoteWorkItemStartupReconciler> log)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;

        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.BaseUrl))
        {
            _log.LogDebug("Remote work item server not configured — skipping startup reconciliation");
            return;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;

            var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
            var recovery = sp.GetRequiredService<IRecoveryManager>();
            var tracker = sp.GetRequiredService<IActiveWorkItemTracker>();
            var client = sp.GetRequiredService<IWorkItemServerClient>();

            var serverOpts = new WorkItemServerOptions
            {
                BaseUrl = opts.BaseUrl,
                ApiKey = opts.ApiKey,
            };

            // Running runs plus runs parked WaitingHuman at a Human/PR node —
            // the engine considers both active.
            var allActive = await loopRunStore.GetActiveRunsAsync();

            var reconciled = 0;
            var resumed = 0;
            var cleaned = 0;

            foreach (var run in allActive)
            {
                var wi = await client.GetAsync(serverOpts, run.WorkItemId, cancellationToken);

                if (wi == null)
                {
                    // Work item no longer exists on server — cancel the orphan
                    // run so a later restart can't resurrect it.
                    await CancelLocalRunAsync(loopRunStore, run, "Work item no longer exists on server");
                    tracker.Remove(run.WorkItemId);
                    cleaned++;
                    _log.LogInformation(
                        "Startup reconcile: work item {WorkItemId} for run {RunId} not found on server — run cancelled",
                        run.WorkItemId, run.Id);
                    continue;
                }

                switch (wi.Status)
                {
                    case RemoteWorkItemStatus.Running:
                        tracker.Add(run.WorkItemId);
                        if (run.Status == LoopRunStatus.Running)
                        {
                            // RecoveryManager honors the run's RecoveryPolicy and
                            // skips runs parked at WaitingHuman nodes or with an
                            // unhealthy worktree.
                            await recovery.RecoverRunAsync(run.Id);
                            resumed++;
                            _log.LogInformation(
                                "Startup reconcile: work item {WorkItemId} still Running on server — recovering run {RunId}",
                                run.WorkItemId, run.Id);
                        }
                        // WaitingHuman runs resume via their pending signal.
                        reconciled++;
                        break;

                    case RemoteWorkItemStatus.HumanFeedback:
                    case RemoteWorkItemStatus.WaitingForIld:
                        // Item is parked — track it so the poller heartbeats it,
                        // but don't resume the engine (human needs to respond
                        // first). Without re-tracking, the stale reclaimer hands
                        // the item to a second concurrent run ~15 minutes after
                        // a human resumes it.
                        tracker.Add(run.WorkItemId);
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} in {Status} — tracked (no resume)",
                            run.WorkItemId, wi.Status);
                        reconciled++;
                        break;

                    case RemoteWorkItemStatus.Done:
                        // Inconsistent: normal completion marks the run terminal
                        // before the item goes Done. Cancel so the run isn't
                        // resurrected by a later restart.
                        await CancelLocalRunAsync(loopRunStore, run, "Work item already Done on server");
                        tracker.Remove(run.WorkItemId);
                        cleaned++;
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} is Done on server — cancelled stale run {RunId}",
                            run.WorkItemId, run.Id);
                        break;

                    default:
                        // Backlog, WorkQueue, Ready — the server reclaimed or
                        // reset the item and will hand it out as a fresh run.
                        // Cancel the local run so two loops never fight over
                        // one work item.
                        await CancelLocalRunAsync(loopRunStore, run, $"Server reset work item to {wi.Status}");
                        tracker.Remove(run.WorkItemId);
                        cleaned++;
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} in {Status} — cancelled superseded run {RunId}",
                            run.WorkItemId, wi.Status, run.Id);
                        break;
                }
            }

            _log.LogInformation(
                "Startup reconciliation complete: {Reconciled} tracked, {Resumed} resumed, {Cleaned} cleaned up",
                reconciled, resumed, cleaned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutting down during reconciliation — nothing to do.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup reconciliation failed — poller will pick up work on next cycle");
        }
    }

    private static async Task CancelLocalRunAsync(ILoopRunStore store, LoopRun run, string reason)
    {
        run.Status = LoopRunStatus.Cancelled;
        run.CompletedAt ??= DateTime.UtcNow;
        run.HumanFeedbackReason = reason;
        await store.UpdateRunAsync(run);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
