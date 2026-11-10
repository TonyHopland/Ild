using System.Threading.Channels;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Where the proxy reports each destination. Recording must not sit on the
/// connection's path — a database write per CONNECT would add its latency to
/// every request the agent makes — so entries are queued and persisted from a
/// single writer, which also announces each one to the UI.
/// </summary>
public interface INetworkLogRecorder
{
    /// <summary>
    /// Queues the entry. False when the queue is full and the entry was not kept: a connection that is allowed must not go
    /// unlogged, so the caller refuses it.
    /// </summary>
    bool Record(string host, int port, NetworkDecision decision, Guid? aiProviderId, string? reason = null, string? installer = null);
}

public sealed class NetworkLogRecorder : BackgroundService, INetworkLogRecorder
{
    private const int QueueCapacity = 4096;

    private readonly Channel<NetworkLogEntry> _queue = Channel.CreateBounded<NetworkLogEntry>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            // A full queue refuses the new entry and keeps the earlier ones: a run flooding the queue cannot push its own earlier connections out.
            FullMode = BoundedChannelFullMode.Wait,
        });
    private static readonly TimeSpan DropReportInterval = TimeSpan.FromMinutes(1);
    private long _dropped;
    private readonly IServiceScopeFactory _scopes;
    private readonly INetworkNotifier _notifier;
    private readonly ILogger<NetworkLogRecorder> _log;
    private readonly InstallerRefusals? _refusals;

    public NetworkLogRecorder(IServiceScopeFactory scopes, INetworkNotifier notifier, ILogger<NetworkLogRecorder> log, InstallerRefusals? refusals = null)
    {
        _refusals = refusals;
        _scopes = scopes;
        _notifier = notifier;
        _log = log;
    }

    public bool Record(string host, int port, NetworkDecision decision, Guid? aiProviderId, string? reason = null, string? installer = null)
    {
        if (installer is not null && decision == NetworkDecision.Blocked) _refusals?.Note(installer, host, port);
        var queued = _queue.Writer.TryWrite(new NetworkLogEntry
        {
            Id = Guid.NewGuid(),
            Host = host.Length > EgressRules.MaxHostLength ? host[..EgressRules.MaxHostLength] : host,
            Port = port,
            Timestamp = DateTime.UtcNow,
            Decision = decision,
            AiProviderId = aiProviderId,
            Reason = reason,
            Installer = installer,
        });
        if (!queued) Interlocked.Increment(ref _dropped);
        return queued;
    }

    /// <summary>Logs how many entries did not fit since the last report, and starts counting again; nothing when there were none.</summary>
    internal void ReportDropped()
    {
        if (Interlocked.Exchange(ref _dropped, 0) is > 0 and var dropped)
            _log.LogWarning("The network log queue was full: {Count} connection entries were not recorded in the last {Seconds} s", dropped, DropReportInterval.TotalSeconds);
    }

    private async Task ReportDropsAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(DropReportInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                ReportDropped();
        }
        catch (OperationCanceledException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = ReportDropsAsync(stoppingToken);
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            var batch = new List<NetworkLogEntry>();
            while (batch.Count < 256 && reader.TryRead(out var entry))
                batch.Add(entry);

            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<INetworkPolicyStore>()
                    .AppendLogAsync(batch, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not persist {Count} network log entries", batch.Count);
                continue;
            }

            foreach (var entry in batch)
                await _notifier.LogEntryAppendedAsync(entry).ConfigureAwait(false);
        }
    }
}
