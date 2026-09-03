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
    void Record(string host, int port, NetworkDecision decision, Guid? aiProviderId);
}

public sealed class NetworkLogRecorder : BackgroundService, INetworkLogRecorder
{
    private const int QueueCapacity = 4096;

    private readonly Channel<NetworkLogEntry> _queue = Channel.CreateBounded<NetworkLogEntry>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly IServiceScopeFactory _scopes;
    private readonly INetworkNotifier _notifier;
    private readonly ILogger<NetworkLogRecorder> _log;

    public NetworkLogRecorder(IServiceScopeFactory scopes, INetworkNotifier notifier, ILogger<NetworkLogRecorder> log)
    {
        _scopes = scopes;
        _notifier = notifier;
        _log = log;
    }

    public void Record(string host, int port, NetworkDecision decision, Guid? aiProviderId)
        => _queue.Writer.TryWrite(new NetworkLogEntry
        {
            Id = Guid.NewGuid(),
            Host = host.Length > EgressRules.MaxHostLength ? host[..EgressRules.MaxHostLength] : host,
            Port = port,
            Timestamp = DateTime.UtcNow,
            Decision = decision,
            AiProviderId = aiProviderId,
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
