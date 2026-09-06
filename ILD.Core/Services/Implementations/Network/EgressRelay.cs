using System.Collections.Concurrent;
using System.Net.Sockets;
using ILD.Data.Enums;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The half of an egress path that is the same however the destination was
/// learned: a client socket joined to an upstream socket byte for byte, with the
/// pairing kept where the policy can reach it.
///
/// <para>
/// <see cref="EgressProxy"/> learns its destination from a <c>CONNECT</c> target,
/// a <c>Host</c> header or a TLS SNI; <see cref="EgressForwarder"/> reads it off a
/// declared forward. Both relay through here, so blacklisting a host mid-transfer
/// resets an open relay of either kind, by one piece of code.
/// </para>
/// </summary>
public sealed class EgressRelay
{
    private readonly IEgressPolicy _policy;
    private readonly INetworkLogRecorder _log;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, Tunnel> _tunnels = new();
    private long _nextTunnelId;

    public EgressRelay(IEgressPolicy policy, INetworkLogRecorder log, ILogger logger)
    {
        _policy = policy;
        _log = log;
        _logger = logger;
    }

    public int OpenCount => _tunnels.Count;

    /// <summary>
    /// Join <paramref name="client"/> to <paramref name="upstream"/> until either
    /// direction ends.
    ///
    /// <para>
    /// The pair is registered against <paramref name="host"/> before
    /// <paramref name="onOpen"/> tells either side anything, so a host blacklisted
    /// from here on is re-judged rather than keeping a live relay nothing can see.
    /// <paramref name="onOpen"/> runs on the relay's own token so that a reset in
    /// that window closes the connection rather than completing a handshake the
    /// policy has already withdrawn.
    /// </para>
    /// </summary>
    public async Task RelayAsync(
        NetworkStream client,
        NetworkStream upstream,
        string host,
        int port,
        Guid? aiProviderId,
        CancellationToken stoppingToken,
        Func<CancellationToken, Task>? onOpen = null)
    {
        var id = Interlocked.Increment(ref _nextTunnelId);
        using var tunnel = new Tunnel(host, port, aiProviderId, stoppingToken);
        _tunnels[id] = tunnel;
        try
        {
            if (onOpen is not null)
                await onOpen(tunnel.Token).ConfigureAwait(false);

            // Each direction ends on its own: a client that half-closes after
            // sending its request must still receive the response, so its EOF is
            // passed on as a send-shutdown rather than ending the whole tunnel.
            // A failure (reset, cancellation) on either side ends both.
            await Task.WhenAll(
                PumpAsync(client, upstream, tunnel),
                PumpAsync(upstream, client, tunnel)).ConfigureAwait(false);
        }
        finally
        {
            _tunnels.TryRemove(id, out _);
            tunnel.Reset();
        }
    }

    /// <summary>
    /// Re-judge every open relay against the lists as they now stand and reset the
    /// ones that have become blocked, recording each as blocked.
    /// </summary>
    public async Task ResetNewlyBlockedAsync()
    {
        try
        {
            var snapshot = await _policy.GetAsync().ConfigureAwait(false);
            foreach (var tunnel in _tunnels.Values)
            {
                if (snapshot.Decide(tunnel.Host, tunnel.AiProviderId) != NetworkDecision.Blocked)
                    continue;
                _log.Record(tunnel.Host, tunnel.Port, NetworkDecision.Blocked, tunnel.AiProviderId);
                tunnel.Reset();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not re-judge open tunnels after a policy change");
        }
    }

    /// <summary>Ends every open relay, whatever the policy says — the shutdown path.</summary>
    public void ResetAll()
    {
        foreach (var tunnel in _tunnels.Values)
            tunnel.Reset();
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, Tunnel tunnel)
    {
        try
        {
            await from.CopyToAsync(to, tunnel.Token).ConfigureAwait(false);
            to.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            tunnel.Reset();
        }
    }

    private sealed class Tunnel : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public Tunnel(string host, int port, Guid? aiProviderId, CancellationToken stopping)
        {
            Host = host;
            Port = port;
            AiProviderId = aiProviderId;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        }

        public string Host { get; }
        public int Port { get; }
        public Guid? AiProviderId { get; }
        public CancellationToken Token => _cts.Token;

        public void Reset()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Dispose() => _cts.Dispose();
    }
}
