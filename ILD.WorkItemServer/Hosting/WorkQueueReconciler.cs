using ILD.WorkItemServer.Services;

namespace ILD.WorkItemServer.Hosting;

/// <summary>
/// Periodically promotes WorkQueue items whose dependencies are all satisfied
/// to Ready. A reconciliation backstop for the event-driven promotion that
/// fires on a dependency's Done transition: it recovers items that entered the
/// work queue with their dependencies already complete, or whose promotion was
/// lost to a crash, cancellation, or concurrent completion. Cadence is
/// configurable via the WorkItemServer:ReconcileScanIntervalSeconds setting.
/// </summary>
public sealed class WorkQueueReconciler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WorkQueueReconciler> _logger;
    private readonly TimeSpan _interval;

    public WorkQueueReconciler(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<WorkQueueReconciler> logger)
    {
        _scopes = scopes;
        _logger = logger;
        var intervalSeconds = config.GetValue<int?>("WorkItemServer:ReconcileScanIntervalSeconds") ?? 30;
        _interval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IWorkItemService>();
                var n = await svc.ReconcileWorkQueueAsync(stoppingToken);
                if (n > 0) _logger.LogInformation("Promoted {Count} ready WorkQueue item(s) to Ready", n);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WorkQueue reconcile pass failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
