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
/// Running), leaves the run parked without resuming (server says
/// HumanFeedback/WaitingForIld — a human has to respond first), or cancels
/// the local run (item gone, Done, or reclaimed by the server) so an
/// orphaned Running row can't be resurrected by a later restart and fight
/// the fresh run.
///
/// Cancelling is what makes the poller's derived active set honest: the
/// scheduler heartbeats exactly the work items behind live local runs, so a
/// run left Running here would keep an item alive on the server forever,
/// and a run wrongly cancelled would drop it out of the heartbeat and let
/// the server hand it to a second concurrent run.
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
            var engine = sp.GetRequiredService<ILoopEngine>();
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
                    await engine.StopRunAsync(run.Id, "Work item no longer exists on server");
                    cleaned++;
                    _log.LogInformation(
                        "Startup reconcile: work item {WorkItemId} for run {RunId} not found on server — run cancelled",
                        run.WorkItemId, run.Id);
                    continue;
                }

                switch (wi.Status)
                {
                    case RemoteWorkItemStatus.Running:
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
                        else if (run.IsShutdownHalted)
                        {
                            // The one WaitingHuman run with no pending signal
                            // coming: what it was waiting for was this process
                            // starting again. Through the recovery manager, so
                            // its RecoveryPolicy and worktree health still get a
                            // say, and it resumes against the agent session the
                            // drain parked it on.
                            await recovery.RecoverRunAsync(run.Id);
                            resumed++;
                            _log.LogInformation(
                                "Startup reconcile: run {RunId} was parked by a shutdown drain and its work item {WorkItemId} is still Running — resuming",
                                run.Id, run.WorkItemId);
                        }
                        else
                        {
                            // WaitingHuman runs resume via their pending signal.
                            // Counted apart from the resumed ones so the tally
                            // below adds up to the number of runs seen.
                            reconciled++;
                        }
                        break;

                    case RemoteWorkItemStatus.HumanFeedback:
                    case RemoteWorkItemStatus.WaitingForIld:
                        // Item is parked: leave the run alive so the poller
                        // keeps heartbeating it, but don't resume the engine (a
                        // human needs to respond first). Cancelling here would
                        // drop the item out of the heartbeat and the stale
                        // reclaimer would hand it to a second concurrent run
                        // ~15 minutes after a human resumes it.
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} in {Status} — left parked (no resume)",
                            run.WorkItemId, wi.Status);
                        reconciled++;
                        break;

                    case RemoteWorkItemStatus.Done:
                        // Inconsistent: normal completion marks the run terminal
                        // before the item goes Done. Cancel so the run isn't
                        // resurrected by a later restart.
                        await engine.StopRunAsync(run.Id, "Work item already Done on server");
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
                        await engine.StopRunAsync(run.Id, $"Server reset work item to {wi.Status}");
                        cleaned++;
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} in {Status} — cancelled superseded run {RunId}",
                            run.WorkItemId, wi.Status, run.Id);
                        break;
                }
            }

            _log.LogInformation(
                "Startup reconciliation complete: {Reconciled} kept alive, {Resumed} resumed, {Cleaned} cleaned up",
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
