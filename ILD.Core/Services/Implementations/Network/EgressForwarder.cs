using System.Net.Sockets;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>Where each declared forward's loopback listener stands, for the UI to show.</summary>
public interface IEgressForwarderState
{
    /// <summary>Why this forward is not answering on its local port, or null when it is.</summary>
    string? ListenErrorFor(Guid forwardId);
}

/// <summary>
/// The loopback listeners behind the declared forwards (<c>docs/adr/0020</c>).
/// A client that cannot name its destination in band — Npgsql, redis-cli, an SMTP
/// library — addresses <c>127.0.0.1:&lt;localPort&gt;</c> instead, and the
/// orchestrator, whose uid the firewall rules never touch, dials the real
/// destination on its behalf.
///
/// <para>
/// A forward is transport, not permission. Every accepted connection is judged by
/// the same <see cref="IEgressPolicy"/> the proxy consults and recorded to the
/// same log under the destination's <em>hostname</em>, which is re-resolved per
/// connection so a rotated address needs no edit. A blocked destination is closed
/// at once rather than left to time out — being refused promptly is the whole
/// reason for answering the connection instead of letting the kernel drop it.
/// Relays are re-judged and reset on a policy change exactly as the proxy's
/// tunnels are, through the shared <see cref="EgressRelay"/>.
/// </para>
///
/// <para>
/// The listener set is reconciled against the rows on every policy change, so an
/// added or deleted forward is live on the next connection. A local port that will
/// not bind is reported against that row alone and retried; every other forward
/// keeps serving.
/// </para>
/// </summary>
public sealed class EgressForwarder : BackgroundService, IEgressForwarderState
{
    private static readonly TimeSpan SettledInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly IEgressPolicy _policy;
    private readonly INetworkLogRecorder _log;
    private readonly INetworkNotifier _notifier;
    private readonly ILogger<EgressForwarder> _logger;
    private readonly EgressRelay _relay;

    private readonly SemaphoreSlim _reconciling = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<Guid, Listener> _listeners = new();
    private volatile IReadOnlyDictionary<Guid, string> _listenErrors = new Dictionary<Guid, string>();
    private CancellationToken _stopping;

    public EgressForwarder(
        IServiceScopeFactory scopes,
        IEgressPolicy policy,
        INetworkLogRecorder log,
        INetworkNotifier notifier,
        ILogger<EgressForwarder> logger)
    {
        _scopes = scopes;
        _policy = policy;
        _log = log;
        _notifier = notifier;
        _logger = logger;
        _relay = new EgressRelay(policy, log, logger);
    }

    public int OpenRelayCount => _relay.OpenCount;

    public string? ListenErrorFor(Guid forwardId)
        => _listenErrors.TryGetValue(forwardId, out var error) ? error : null;

    /// <summary>The local ports currently bound and serving; the test seam for "is it listening".</summary>
    public IReadOnlyCollection<int> ListeningPorts
    {
        get { lock (_listeners) return _listeners.Values.Select(l => l.Port).ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _policy.Changed += OnPolicyChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settled = await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                // A port held by something else, or a database not up yet, is
                // retried on its own rather than waiting for an edit that may
                // never come.
                await _wake.WaitAsync(settled ? SettledInterval : RetryInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _policy.Changed -= OnPolicyChanged;
            await CloseAllAsync().ConfigureAwait(false);
            _relay.ResetAll();
        }
    }

    private void OnPolicyChanged()
    {
        _ = _relay.ResetNewlyBlockedAsync();
        Wake();
    }

    /// <summary>
    /// Ask for a reconcile without waiting for one. Called from the thread that
    /// edited the policy, so it must not throw: a wake already pending, or one
    /// arriving after shutdown, is nothing to report.
    /// </summary>
    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Bring the bound listeners in line with the declared rows. Answers whether
    /// everything declared is now serving, which is what decides how soon this
    /// runs again unprompted.
    /// </summary>
    private async Task<bool> ReconcileAsync(CancellationToken ct)
    {
        await _reconciling.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<NetworkForwardEntry> declared;
            try
            {
                using var scope = _scopes.CreateScope();
                declared = await scope.ServiceProvider.GetRequiredService<INetworkForwardStore>()
                    .GetForwardsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read the declared forwards; the listeners already bound keep serving");
                return false;
            }

            var errors = new Dictionary<Guid, string>();
            lock (_listeners)
            {
                foreach (var (id, listener) in _listeners.ToList())
                {
                    if (declared.Any(f => f.Id == id && listener.Serves(f))) continue;
                    listener.Dispose();
                    _listeners.Remove(id);
                }

                foreach (var forward in declared)
                {
                    if (_listeners.ContainsKey(forward.Id)) continue;
                    if (TryBind(forward, out var listener, out var error)) _listeners[forward.Id] = listener;
                    else errors[forward.Id] = error;
                }
            }

            await PublishListenErrorsAsync(errors).ConfigureAwait(false);
            return errors.Count == 0;
        }
        finally
        {
            _reconciling.Release();
        }
    }

    private bool TryBind(NetworkForwardEntry forward, out Listener listener, out string error)
    {
        listener = null!;
        var socket = new TcpListener(EgressProxyOptions.ListenAddress, forward.LocalPort);
        try
        {
            socket.Start();
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Local port {forward.LocalPort} is already in use by something else"
                : $"Could not listen on {EgressProxyOptions.ListenAddress}:{forward.LocalPort} ({ex.SocketErrorCode})";

            // A squatted port is retried for as long as it stays squatted, and a
            // line every retry says nothing the first one did not.
            if (!_listenErrors.TryGetValue(forward.Id, out var reported) || reported != error)
            {
                _logger.LogError(ex, "Forward {Name} could not listen on {Address}:{Port}",
                    forward.Name, EgressProxyOptions.ListenAddress, forward.LocalPort);
            }
            return false;
        }

        listener = new Listener(forward, socket, _stopping);
        _ = AcceptAsync(listener);
        _logger.LogInformation("Forward {Name} listening on {Address}:{LocalPort} for {Host}:{Port}",
            forward.Name, EgressProxyOptions.ListenAddress, forward.LocalPort, forward.Host, forward.Port);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Announce a change in which forwards are serving, so an open Settings page
    /// learns a port is unavailable without being told to look again.
    /// </summary>
    private async Task PublishListenErrorsAsync(IReadOnlyDictionary<Guid, string> errors)
    {
        var previous = _listenErrors;
        if (previous.Count == errors.Count
            && previous.All(e => errors.TryGetValue(e.Key, out var now) && now == e.Value))
            return;

        _listenErrors = errors;
        try
        {
            await _notifier.PolicyChangedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not announce the forward listener state");
        }
    }

    private async Task CloseAllAsync()
    {
        await _reconciling.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_listeners)
            {
                foreach (var listener in _listeners.Values) listener.Dispose();
                _listeners.Clear();
            }
            _listenErrors = new Dictionary<Guid, string>();
        }
        finally
        {
            _reconciling.Release();
        }
    }

    /// <summary>
    /// A client that walks away between the handshake and the accept fails that
    /// one accept and nothing else. Every other socket error is about the
    /// listener rather than the caller, and retrying those is what turns a dead
    /// listener into a spinning one.
    /// </summary>
    internal static bool IsAbandonedHandshake(SocketError error)
        => error is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Interrupted;

    private async Task AcceptAsync(Listener listener)
    {
        // Read once: the token outlives its source's disposal, and the source is
        // disposed the moment this listener is taken out of service.
        var token = listener.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.Socket.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (SocketException ex) when (IsAbandonedHandshake(ex.SocketErrorCode))
            {
                _logger.LogDebug(ex, "Forward {Name} lost a connection before accepting it ({Error})",
                    listener.Forward.Name, ex.SocketErrorCode);
                continue;
            }
            catch (SocketException ex)
            {
                // Persistent — the file-descriptor ceiling, a socket left in a
                // failing state. Looping on it would burn a core while the row
                // went on claiming to listen on a port serving nothing.
                _logger.LogError(ex, "Forward {Name} stopped accepting on {Address}:{Port} ({Error})",
                    listener.Forward.Name, EgressProxyOptions.ListenAddress, listener.Port, ex.SocketErrorCode);
                Retire(listener, $"Stopped accepting on local port {listener.Port} ({ex.SocketErrorCode})");
                return;
            }
            catch (Exception)
            {
                return;
            }
            _ = HandleConnectionAsync(listener.Forward, client, token);
        }
    }

    /// <summary>
    /// Take a listener out of service and say why, then ask for a reconcile. Not
    /// a verdict — the next pass rebinds the port and either clears the error or
    /// reports why it could not. What this buys is that the row stops claiming to
    /// listen in the meantime.
    /// </summary>
    private void Retire(Listener listener, string error)
    {
        lock (_listeners)
        {
            if (!_listeners.TryGetValue(listener.Forward.Id, out var current) || !ReferenceEquals(current, listener))
                return;
            _listeners.Remove(listener.Forward.Id);
        }
        listener.Dispose();
        _listenErrors = new Dictionary<Guid, string>(_listenErrors) { [listener.Forward.Id] = error };
        Wake();
    }

    private async Task HandleConnectionAsync(NetworkForwardEntry forward, TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                var snapshot = await _policy.GetAsync(ct).ConfigureAwait(false);
                var decision = snapshot.Decide(forward.Host, aiProviderId: null);
                _log.Record(forward.Host, forward.Port, decision, aiProviderId: null);
                if (decision == NetworkDecision.Blocked) return;

                using var upstream = new TcpClient { NoDelay = true };
                try
                {
                    await upstream.ConnectAsync(forward.Host, forward.Port, ct).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    _logger.LogDebug(ex, "Forward {Name} could not reach {Host}:{Port} ({Error})",
                        forward.Name, forward.Host, forward.Port, ex.SocketErrorCode);
                    return;
                }

                try
                {
                    await _relay.RelayAsync(client.GetStream(), upstream.GetStream(),
                        forward.Host, forward.Port, aiProviderId: null, ct).ConfigureAwait(false);
                }
                finally
                {
                    upstream.Close();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Forward {Name} connection failed", forward.Name);
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _reconciling.Dispose();
        _wake.Dispose();
    }

    private sealed class Listener : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public Listener(NetworkForwardEntry forward, TcpListener socket, CancellationToken stopping)
        {
            Forward = forward;
            Socket = socket;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        }

        public NetworkForwardEntry Forward { get; }
        public TcpListener Socket { get; }
        public int Port => Forward.LocalPort;
        public CancellationToken Token => _cts.Token;

        /// <summary>Whether this listener still serves the row as it now reads.</summary>
        public bool Serves(NetworkForwardEntry forward)
            => forward.LocalPort == Forward.LocalPort
                && forward.Port == Forward.Port
                && forward.Host == Forward.Host;

        public void Dispose()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            Socket.Dispose();
            _cts.Dispose();
        }
    }
}
