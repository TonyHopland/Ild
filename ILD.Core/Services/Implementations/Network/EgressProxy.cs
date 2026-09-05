using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ILD.Data.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The forward proxy every agent launch is pointed at (<c>docs/adr/0019</c>).
/// It listens on loopback as the orchestrator, learns the destination host from
/// the <c>CONNECT</c> target, the <c>Host</c> header of a plain request, or the
/// TLS ClientHello SNI of a flow redirected to it, asks <see cref="IEgressPolicy"/>
/// whether that host is allowed, records the destination whatever the answer,
/// and either relays bytes untouched or answers <c>403</c>. Nothing is decrypted.
///
/// <para>
/// Per-provider scope rides on the proxy URL's credentials: a launch made for a
/// provider gets <c>http://provider:&lt;id&gt;@127.0.0.1:port</c>, so the
/// <c>Proxy-Authorization</c> header names the provider whose scoped entries
/// apply. A connection without one is judged by the global entries alone.
/// </para>
///
/// <para>
/// Open tunnels are tracked by host. When the policy changes each is re-judged
/// and a now-blocked one is reset, so blacklisting a host ends a transfer already
/// in progress rather than letting it finish.
/// </para>
/// </summary>
public sealed class EgressProxy : BackgroundService
{
    private const int MaxHeadBytes = 64 * 1024;
    private const int TlsDefaultPort = 443;
    private const int HttpDefaultPort = 80;

    private readonly EgressProxyOptions _options;
    private readonly IEgressPolicy _policy;
    private readonly INetworkLogRecorder _log;
    private readonly ILogger<EgressProxy> _logger;
    private readonly ConcurrentDictionary<long, Tunnel> _tunnels = new();
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextTunnelId;

    public EgressProxy(EgressProxyOptions options, IEgressPolicy policy, INetworkLogRecorder log, ILogger<EgressProxy> logger)
    {
        _options = options;
        _policy = policy;
        _log = log;
        _logger = logger;
    }

    /// <summary>The port actually bound, once listening; lets a test ask for port 0.</summary>
    public Task<int> BoundPort => _bound.Task;

    public int OpenTunnelCount => _tunnels.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _bound.TrySetCanceled();
            return;
        }

        var listener = new TcpListener(EgressProxyOptions.ListenAddress, _options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Egress proxy could not listen on {Address}:{Port}; agent launches will point at a port nothing answers",
                EgressProxyOptions.ListenAddress, _options.Port);
            _bound.TrySetException(ex);
            return;
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _bound.TrySetResult(port);
        _logger.LogInformation("Egress proxy listening on {Address}:{Port}", EgressProxyOptions.ListenAddress, port);

        _policy.Changed += OnPolicyChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _ = HandleConnectionAsync(client, stoppingToken);
            }
        }
        finally
        {
            _policy.Changed -= OnPolicyChanged;
            listener.Stop();
            foreach (var tunnel in _tunnels.Values)
                tunnel.Reset();
        }
    }

    private void OnPolicyChanged() => _ = ResetNewlyBlockedTunnelsAsync();

    private async Task ResetNewlyBlockedTunnelsAsync()
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

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, stoppingToken).ConfigureAwait(false);
                if (head is null) return;

                var request = ParseRequest(head.Value);
                if (request is null)
                {
                    await WriteResponseAsync(stream, "400 Bad Request", "The egress proxy could not parse this request.", stoppingToken).ConfigureAwait(false);
                    return;
                }

                var snapshot = await _policy.GetAsync(stoppingToken).ConfigureAwait(false);
                var decision = snapshot.Decide(request.Host, request.AiProviderId);
                _log.Record(request.Host, request.Port, decision, request.AiProviderId);

                if (decision == NetworkDecision.Blocked)
                {
                    await RefuseAsync(stream, request, stoppingToken).ConfigureAwait(false);
                    return;
                }

                await RelayAsync(stream, request, head.Value, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Egress proxy connection failed");
            }
        }
    }

    private async Task RelayAsync(NetworkStream client, ProxyRequest request, ArraySegment<byte> head, CancellationToken stoppingToken)
    {
        using var upstream = new TcpClient { NoDelay = true };
        try
        {
            await upstream.ConnectAsync(request.Host, request.Port, stoppingToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            if (request.Kind != RequestKind.Tls)
                await WriteResponseAsync(client, "502 Bad Gateway", $"The egress proxy could not reach {request.Host}:{request.Port} ({ex.SocketErrorCode}).", stoppingToken).ConfigureAwait(false);
            return;
        }

        // Registered before the handshake reaches either side, so a host
        // blacklisted from here on is re-judged rather than keeping a live relay
        // nothing can see. Everything past this point runs on the tunnel's token
        // so that a reset in this window closes the connection rather than
        // completing a handshake the policy has already withdrawn.
        var id = Interlocked.Increment(ref _nextTunnelId);
        using var tunnel = new Tunnel(request.Host, request.Port, request.AiProviderId, stoppingToken);
        _tunnels[id] = tunnel;
        try
        {
            var upstreamStream = upstream.GetStream();
            switch (request.Kind)
            {
                case RequestKind.Connect:
                    await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), tunnel.Token).ConfigureAwait(false);
                    if (request.BodyStart < head.Count)
                        await upstreamStream.WriteAsync(head.AsMemory(request.BodyStart), tunnel.Token).ConfigureAwait(false);
                    break;
                case RequestKind.Tls:
                    await upstreamStream.WriteAsync(head, tunnel.Token).ConfigureAwait(false);
                    break;
                default:
                    await upstreamStream.WriteAsync(request.ForwardedHead, tunnel.Token).ConfigureAwait(false);
                    if (request.BodyStart < head.Count)
                        await upstreamStream.WriteAsync(head.AsMemory(request.BodyStart), tunnel.Token).ConfigureAwait(false);
                    break;
            }

            // Each direction ends on its own: a client that half-closes after
            // sending its request must still receive the response, so its EOF is
            // passed on as a send-shutdown rather than ending the whole tunnel.
            // A failure (reset, cancellation) on either side ends both.
            await Task.WhenAll(
                PumpAsync(client, upstreamStream, tunnel),
                PumpAsync(upstreamStream, client, tunnel)).ConfigureAwait(false);
        }
        finally
        {
            _tunnels.TryRemove(id, out _);
            tunnel.Reset();
            upstream.Close();
        }
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

    private static async Task RefuseAsync(NetworkStream client, ProxyRequest request, CancellationToken ct)
    {
        if (request.Kind == RequestKind.Tls)
        {
            // access_denied alert: the only refusal a TLS client can read.
            await client.WriteAsync(new byte[] { 0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x31 }, ct).ConfigureAwait(false);
            return;
        }
        await WriteResponseAsync(client, "403 Forbidden", $"ILD network policy blocks {request.Host}.", ct).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(NetworkStream client, string status, string body, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body + "\n");
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nProxy-Connection: close\r\n\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await client.WriteAsync(payload, ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read enough of the connection to know where it is going: a whole TLS
    /// record for a redirected flow, or through the blank line ending the HTTP
    /// head otherwise. Returns null on a closed or oversized head.
    /// </summary>
    private static async Task<ArraySegment<byte>?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) return null;
            filled += read;

            var span = buffer.AsSpan(0, filled);
            if (TlsClientHello.StartsHandshake(span))
            {
                if (TlsClientHello.RecordLength(span) is { } length && filled >= length)
                    return new ArraySegment<byte>(buffer, 0, filled);
                continue;
            }
            if (span.IndexOf("\r\n\r\n"u8) >= 0)
                return new ArraySegment<byte>(buffer, 0, filled);
        }
        return null;
    }

    internal static ProxyRequest? ParseRequest(ArraySegment<byte> head)
    {
        var span = head.AsSpan();
        if (TlsClientHello.StartsHandshake(span))
        {
            var sni = TlsClientHello.ReadServerName(span);
            return sni is null ? null : new ProxyRequest(RequestKind.Tls, EgressRules.NormalizeHost(sni), TlsDefaultPort, null, head.Count, Array.Empty<byte>());
        }

        var terminator = span.IndexOf("\r\n\r\n"u8);
        if (terminator < 0) return null;
        var bodyStart = terminator + 4;
        var lines = Encoding.ASCII.GetString(span[..terminator]).Split("\r\n");
        var requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length != 3) return null;

        var (method, target, version) = (requestLine[0], requestLine[1], requestLine[2]);
        Guid? provider = null;
        string? hostHeader = null;
        var forwarded = new StringBuilder();
        var sawHostHeader = false;

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                provider ??= ReadProviderScope(value);
                continue;
            }
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostHeader = value;
                sawHostHeader = true;
            }
            forwarded.Append(line).Append("\r\n");
        }

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TrySplitHostPort(target, TlsDefaultPort, out var connectHost, out var connectPort)) return null;
            return new ProxyRequest(RequestKind.Connect, connectHost, connectPort, provider, bodyStart, Array.Empty<byte>());
        }

        string host;
        int port;
        string originTarget;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http")
        {
            host = EgressRules.NormalizeHost(uri.Host);
            port = uri.IsDefaultPort ? HttpDefaultPort : uri.Port;
            originTarget = uri.PathAndQuery.Length == 0 ? "/" : uri.PathAndQuery;
            hostHeader ??= uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
        else if (target.StartsWith('/') && hostHeader is not null)
        {
            if (!TrySplitHostPort(hostHeader, HttpDefaultPort, out host, out port)) return null;
            originTarget = target;
        }
        else
        {
            return null;
        }

        // An absolute-form request may legitimately omit Host (the authority is in
        // the target); the origin-form request the upstream gets may not.
        var headText = new StringBuilder()
            .Append(method).Append(' ').Append(originTarget).Append(' ').Append(version).Append("\r\n")
            .Append(sawHostHeader ? string.Empty : "Host: " + hostHeader + "\r\n")
            .Append(forwarded)
            .Append("Connection: close\r\n\r\n")
            .ToString();
        return new ProxyRequest(RequestKind.Http, host, port, provider, bodyStart, Encoding.ASCII.GetBytes(headText));
    }

    /// <summary>
    /// The provider a launch was made for, carried as the Basic password of the
    /// proxy URL (<see cref="EgressProxyOptions.ClientUrl"/>) behind the fixed
    /// <see cref="EgressProxyOptions.ScopeUser"/> name. Credentials of any other
    /// shape are somebody else's and attribute nothing.
    /// </summary>
    internal static Guid? ReadProviderScope(string proxyAuthorization)
    {
        var parts = proxyAuthorization.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("Basic", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            var colon = credentials.IndexOf(':');
            if (colon < 0 || !credentials.AsSpan(0, colon).SequenceEqual(EgressProxyOptions.ScopeUser)) return null;
            return Guid.TryParse(credentials.AsSpan(colon + 1), out var id) ? id : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool TrySplitHostPort(string authority, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        if (authority.Length == 0) return false;

        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;
            host = EgressRules.NormalizeHost(authority[..(close + 1)]);
            var rest = authority[(close + 1)..];
            return rest.Length == 0 || (rest[0] == ':' && int.TryParse(rest[1..], out port) && port is > 0 and <= 65535);
        }

        var lastColon = authority.LastIndexOf(':');
        if (lastColon < 0 || authority.IndexOf(':') != lastColon)
        {
            host = EgressRules.NormalizeHost(authority);
            return host.Length > 0;
        }
        host = EgressRules.NormalizeHost(authority[..lastColon]);
        return host.Length > 0 && int.TryParse(authority[(lastColon + 1)..], out port) && port is > 0 and <= 65535;
    }

    internal enum RequestKind { Connect, Http, Tls }

    internal sealed record ProxyRequest(RequestKind Kind, string Host, int Port, Guid? AiProviderId, int BodyStart, byte[] ForwardedHead);

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
