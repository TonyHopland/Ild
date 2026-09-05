using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Caps the size of the <c>NetworkLogEntries</c> table, which the proxy appends
/// to on every agent connection and nothing else ever trimmed. Rows older than
/// the configured <see cref="AppSettingKeys.NetworkLogRetentionDays"/> window
/// are deleted outright; <c>0</c> disables the sweep and keeps the log forever.
///
/// The window is read from settings on every pass, so changing it in the UI
/// takes effect on the next sweep without a restart. A sweep that removed
/// anything announces it so an open log view re-reads what survived rather than
/// showing rows that are gone.
/// </summary>
public sealed class NetworkLogRetentionSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly INetworkNotifier _notifier;
    private readonly ILogger<NetworkLogRetentionSweeper> _log;

    public NetworkLogRetentionSweeper(
        IServiceScopeFactory scopes,
        INetworkNotifier notifier,
        ILogger<NetworkLogRetentionSweeper> log)
    {
        _scopes = scopes;
        _notifier = notifier;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                _log.LogError(ex, "Network log retention sweep failed");
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var settings = sp.GetRequiredService<ISchedulerSettingsService>();
        var store = sp.GetRequiredService<INetworkPolicyStore>();

        var retentionDays = await settings.GetNetworkLogRetentionDaysAsync(ct);
        if (retentionDays <= 0) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
        var removed = await store.DeleteLogOlderThanAsync(cutoff, ct);
        if (removed == 0) return;

        _log.LogInformation("Network log retention swept {Removed} entries older than {Cutoff:o}", removed, cutoff);
        await _notifier.LogClearedAsync();
    }
}
