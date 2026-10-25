using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Services;

namespace ILD.WorkItemServer.Hosting;

/// <summary>
/// Periodically reclaims work items whose claiming ILD instance has stopped
/// heartbeating. Default cadence and timeout are configurable via the
/// WorkItemServer:StaleTimeoutMinutes / StaleScanIntervalSeconds settings.
/// </summary>
public sealed class StaleWorkItemReclaimer : BackgroundService
{
    private static readonly WorkItemActor Actor = new("system", WorkItemActorKind.System, "system: stale reclaim", null);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StaleWorkItemReclaimer> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly ImportGate _importGate;

    public StaleWorkItemReclaimer(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<StaleWorkItemReclaimer> logger,
        ImportGate? importGate = null)
    {
        _scopes = scopes;
        _logger = logger;
        _importGate = importGate ?? new ImportGate();
        var timeoutMinutes = config.GetValue<int?>("WorkItemServer:StaleTimeoutMinutes") ?? 15;
        var intervalSeconds = config.GetValue<int?>("WorkItemServer:StaleScanIntervalSeconds") ?? 30;
        _timeout = TimeSpan.FromMinutes(timeoutMinutes);
        _interval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // An import in progress holds the gate, and the rows it restores carry heartbeats
                // from when the backup was made: a pass now would reclaim every claimed item.
                using var pass = _importGate.TryEnterPass();
                if (pass is not null)
                {
                    using var scope = _scopes.CreateScope();
                    scope.ServiceProvider.GetRequiredService<WorkItemServerDbContext>().HistoryActor = Actor;
                    var svc = scope.ServiceProvider.GetRequiredService<IWorkItemService>();
                    var n = await svc.ReclaimStaleAsync(_timeout, stoppingToken);
                    if (n > 0) _logger.LogInformation("Reclaimed {Count} stale work item(s) to Ready", n);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stale reclaim pass failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
