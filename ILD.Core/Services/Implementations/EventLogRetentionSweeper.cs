using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Periodically sweeps expired event-log rows. An entry is only deleted
/// when its LoopRun's linked WorkItem is in <c>RemoteWorkItemStatus.Done</c>;
/// runs whose WorkItem is still active (Running, HumanFeedback, etc.) are
/// preserved indefinitely. Runs every <see cref="EventLogOptions.RetentionSweepInterval"/>
/// against a <see cref="EventLogOptions.RetentionPeriod"/> cutoff.
/// </summary>
public sealed class EventLogRetentionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly EventLogOptions _options;
    private readonly ILogger<EventLogRetentionSweeper> _log;

    public EventLogRetentionSweeper(
        IServiceScopeFactory scopes,
        EventLogOptions options,
        ILogger<EventLogRetentionSweeper> log)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Event log retention sweep failed");
            }

            try { await Task.Delay(_options.RetentionSweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogService>();
        var eventLogStore = scope.ServiceProvider.GetRequiredService<IEventLogStore>();
        var runStore = scope.ServiceProvider.GetRequiredService<ILoopRunStore>();
        var workItems = scope.ServiceProvider.GetRequiredService<IWorkItemManager>();

        var cutoff = DateTimeOffset.UtcNow - _options.RetentionPeriod;
        var older = await eventLogStore.GetOlderThanAsync(cutoff);
        if (older.Count == 0) return;

        var runIds = older
            .Where(e => e.LoopRunId.HasValue)
            .Select(e => e.LoopRunId!.Value)
            .Distinct()
            .ToList();

        var eligible = new HashSet<Guid>();
        foreach (var runId in runIds)
        {
            if (ct.IsCancellationRequested) return;
            var run = await runStore.GetByIdAsync(runId);
            if (run is null) continue;
            var wi = await workItems.GetWorkItemAsync(run.WorkItemId);
            if (wi is not null && wi.Status == RemoteWorkItemStatus.Done)
                eligible.Add(runId);
        }

        var removed = await eventLog.EnforceRetentionPolicyAsync(cutoff, eligible);
        if (removed > 0)
            _log.LogInformation("Event log retention swept {Removed} entries older than {Cutoff:o}", removed, cutoff);
    }
}
