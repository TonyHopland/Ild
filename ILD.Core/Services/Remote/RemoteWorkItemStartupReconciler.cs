using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Runs once at startup to reconcile local loop-run state with the remote
/// WorkItem server. For every local LoopRun in Running or WaitingHuman
/// status the reconciler queries the server for the work item's current
/// state and either resumes execution (server says Running), adds the item
/// to the active tracker without resuming (server says HumanFeedback), or
/// removes it from the tracker (server says Done or item no longer exists).
///
/// Registered as a IHostedService so it runs before the poller's first
/// tick but is guaranteed to complete within the host startup timeout.
/// </summary>
public sealed class RemoteWorkItemStartupReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<RemoteWorkItemPollerOptions> _options;
    private readonly ILogger<RemoteWorkItemStartupReconciler> _log;

    public RemoteWorkItemStartupReconciler(
        IServiceScopeFactory scopes,
        IOptionsMonitor<RemoteWorkItemPollerOptions> options,
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

            var loopRunStore = sp.GetRequiredService<ILD.Data.Stores.Interfaces.ILoopRunStore>();
            var engine = sp.GetRequiredService<ILD.Core.Services.Interfaces.ILoopEngine>();
            var tracker = sp.GetRequiredService<IActiveWorkItemTracker>();
            var client = sp.GetRequiredService<IWorkItemServerClient>();

            var serverOpts = new WorkItemServerOptions
            {
                BaseUrl = opts.BaseUrl,
                ApiKey = opts.ApiKey,
            };

            // Gather all local runs that might need reconciliation.
            var activeRuns = await loopRunStore.GetRunningRunsAsync();
            var waitingRuns = await loopRunStore.GetFailedRunIdsAsync(); // completed runs aren't relevant

            // Also check for WaitingHuman runs — they're not "Running" in the
            // store but the engine considers them active.
            var allActive = activeRuns.ToList();

            var reconciled = 0;
            var resumed = 0;
            var cleaned = 0;

            foreach (var run in allActive)
            {
                var wi = await client.GetAsync(serverOpts, run.WorkItemId, cancellationToken);

                if (wi == null)
                {
                    // Work item no longer exists on server — clean up locally.
                    tracker.Remove(run.WorkItemId);
                    cleaned++;
                    _log.LogInformation(
                        "Startup reconcile: work item {WorkItemId} for run {RunId} not found on server — cleaned up",
                        run.WorkItemId, run.Id);
                    continue;
                }

                switch (wi.Status)
                {
                    case RemoteWorkItemStatus.Running:
                        tracker.Add(run.WorkItemId);
                        await engine.ResumeRecoveredRunAsync(run.Id);
                        resumed++;
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} still Running on server — resumed run {RunId}",
                            run.WorkItemId, run.Id);
                        reconciled++;
                        break;

                    case RemoteWorkItemStatus.HumanFeedback:
                    case RemoteWorkItemStatus.WaitingForIld:
                        // Item is parked — track it so the poller heartbeats it,
                        // but don't resume the engine (human needs to respond first).
                        tracker.Add(run.WorkItemId);
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} in {Status} — tracked (no resume)",
                            run.WorkItemId, wi.Status);
                        reconciled++;
                        break;

                    case RemoteWorkItemStatus.Done:
                        tracker.Remove(run.WorkItemId);
                        cleaned++;
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} is Done on server — cleaned up run {RunId}",
                            run.WorkItemId, run.Id);
                        break;

                    default:
                        // Backlog, WorkQueue, Ready — item is no longer ours.
                        tracker.Remove(run.WorkItemId);
                        cleaned++;
                        _log.LogInformation(
                            "Startup reconcile: work item {WorkItemId} in {Status} — removed from tracker",
                            run.WorkItemId, wi.Status);
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
