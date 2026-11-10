using System.Buffers;
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
/// A plain-HTTP connection carries one request: the response head is rewritten
/// to say <c>Connection: close</c>, and nothing the client sends after the first
/// request's body reaches the upstream, so a follow-up request comes on a new
/// connection that is judged and recorded like the first
/// (<see cref="PlainHttpExchange"/>).
/// </para>
///
/// <para>
/// Every launch is pointed at <c>http://ild:&lt;token&gt;@127.0.0.1:port</c>, the token being one ILD signed (<see cref="EgressCredentials"/>)
/// for that launch: a provider's, whose scoped entries apply, an installer's zone, whose entries and log name it, or neither. The scope
/// is read from the verified token alone. A connection with no valid token is refused with <c>407</c> (an <c>access_denied</c> alert for
/// a redirected TLS flow, which cannot carry one) and logged as blocked.
/// </para>

/// <para>
/// Open tunnels are tracked by host. When the policy changes each is re-judged
/// and a now-blocked one is reset, so blacklisting a host ends a transfer already
/// in progress rather than letting it finish.
/// </para>
/// </summary>
public sealed class EgressProxy : BackgroundService
{
    internal const int MaxHeadBytes = 64 * 1024;
    private const int InitialHeadBytes = 4 * 1024;
    private const int TlsDefaultPort = 443;
    private const int HttpDefaultPort = 80;

    private readonly EgressProxyOptions _options;
    private readonly EgressCredentials _credentials;
    private readonly IEgressPolicy _policy;
    private readonly INetworkLogRecorder _log;
    private readonly ILogger<EgressProxy> _logger;
    private readonly EgressRelay _relay;
    private readonly ConnectionLimiter _limiter;
    private readonly Dictionary<IPAddress, DateTime> _refusalLogged = new();
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPAddress[], int, CancellationToken, Task<TcpClient>> _dial;
    private readonly TaskCompletionSource<int> _bound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EgressProxy(EgressProxyOptions options, EgressCredentials credentials, IEgressPolicy policy, INetworkLogRecorder log, ILogger<EgressProxy> logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<IPAddress[], int, CancellationToken, Task<TcpClient>>? dial = null)
    {
        _resolve = resolve ?? EgressDestinationGuard.ResolveAsync;
        _dial = dial ?? EgressDestinationGuard.ConnectAsync;
        _options = options;
        _credentials = credentials;
        _policy = policy;
        _log = log;
        _logger = logger;
        _relay = new EgressRelay(policy, log, logger, options.Limits.IdleTimeout);
        _limiter = new ConnectionLimiter(options.Limits);
    }

    /// <summary>The port actually bound, once listening; lets a test ask for port 0.</summary>
    public Task<int> BoundPort => _bound.Task;

    public int OpenTunnelCount => _relay.OpenCount;

    /// <summary>The accept call on the listening socket; the test seam for an accept that fails.</summary>
    internal Func<TcpListener, CancellationToken, ValueTask<TcpClient>> AcceptClient { get; set; } = static (s, ct) => s.AcceptTcpClientAsync(ct);

    /// <summary>How long the loop waits after an accept fails for a reason that is not the client's, so a descriptor ceiling is not a spin.</summary>
    internal TimeSpan AcceptRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _bound.TrySetCanceled();
            return;
        }

        var listener = new TcpListener(_options.BindAddress, _options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Egress proxy could not listen on {Address}:{Port}; agent launches will point at a port nothing answers",
                _options.BindAddress, _options.Port);
            _bound.TrySetException(ex);
            return;
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _bound.TrySetResult(port);
        _logger.LogInformation("Egress proxy listening on {Address}:{Port}", _options.BindAddress, port);

        _policy.Changed += OnPolicyChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await AcceptClient(listener, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (EgressForwarder.IsAbandonedHandshake(ex.SocketErrorCode))
                {
                    _logger.LogDebug(ex, "Egress proxy lost a connection before accepting it ({Error})", ex.SocketErrorCode);
                    continue;
                }
                catch (SocketException ex)
                {
                    _logger.LogError(ex, "Egress proxy could not accept a connection ({Error}); trying again", ex.SocketErrorCode);
                    try { await Task.Delay(AcceptRetryDelay, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                if (_options.AcceptsClient is { } accepts && !(client.Client.RemoteEndPoint is IPEndPoint peer && accepts(peer.Address)))
                {
                    client.Dispose();
                    continue;
                }
                var source = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                if (_limiter.TryAcquire(source) is not { } lease)
                {
                    LogRefusal(source);
                    client.Dispose();
                    continue;
                }
                _ = HandleConnectionAsync(client, lease, stoppingToken);
            }
        }
        finally
        {
            _policy.Changed -= OnPolicyChanged;
            listener.Stop();
            _relay.ResetAll();
        }
    }

    private void OnPolicyChanged() => _ = _relay.ResetNewlyBlockedAsync();

    private void LogRefusal(IPAddress source)
    {
        lock (_refusalLogged)
        {
            var now = DateTime.UtcNow;
            if (_refusalLogged.TryGetValue(source, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
            _refusalLogged[source] = now;
            if (_refusalLogged.Count > 1024)
                foreach (var stale in _refusalLogged.Where(e => now - e.Value >= TimeSpan.FromMinutes(1)).Select(e => e.Key).ToList())
                    _refusalLogged.Remove(stale);
        }
        _logger.LogWarning("Egress proxy refused a connection from {Source}: too many connections open", source);
    }

    private async Task HandleConnectionAsync(TcpClient client, IDisposable lease, CancellationToken stoppingToken)
    {
        using (lease)
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, _options.Limits.HeadTimeout, stoppingToken).ConfigureAwait(false);
                if (head is null) return;

                var request = ParseRequest(head.Value);
                if (request is null)
                {
                    await WriteResponseAsync(stream, "400 Bad Request", "The egress proxy could not parse this request.", stoppingToken).ConfigureAwait(false);
                    return;
                }

                // Only a launch ILD made holds a credential it signed; the scope is read from that and from nothing the client says.
                if (!_credentials.TryRead(request.Credential, out var scope))
                {
                    _log.Record(request.Host, request.Port, NetworkDecision.Blocked, null, "no valid proxy credential");
                    if (request.Kind == RequestKind.Tls)
                        await RefuseAsync(stream, request, string.Empty, stoppingToken).ConfigureAwait(false);
                    else
                        await WriteResponseAsync(stream, "407 Proxy Authentication Required", "The egress proxy needs the credential ILD gave this launch in its proxy URL.", stoppingToken, "Proxy-Authenticate: Basic realm=\"ILD egress proxy\"\r\n").ConfigureAwait(false);
                    return;
                }
                request = request with { AiProviderId = scope.Provider, Installer = scope.Installer };

                // An agents' preview hostname is ILD's own preview proxy, not a host on the network: handed to ILD's
                // web port on loopback under no network policy, with the preview host as its Host so it reaches nothing else of ILD's.
                if (Interfaces.PreviewProxyBase.IsAgentHost(request.Host))
                {
                    if (request.Kind != RequestKind.Http || _options.WebPort is not int webPort)
                    {
                        await WriteResponseAsync(stream, "400 Bad Request",
                            request.Kind != RequestKind.Http
                                ? $"{request.Host} is a worktree preview: open it over plain http://, not https://."
                                : $"{request.Host} is a worktree preview, but ILD listens on no plain-HTTP port the proxy can hand it to.",
                            stoppingToken).ConfigureAwait(false);
                        return;
                    }
                    var handOff = ParseRequest(head.Value, request.Host)! with { Port = webPort };
                    await RelayAsync(stream, handOff, [IPAddress.Loopback], head.Value, stoppingToken).ConfigureAwait(false);
                    return;
                }

                var snapshot = await _policy.GetAsync(stoppingToken).ConfigureAwait(false);
                var decision = snapshot.Decide(request.Host, request.AiProviderId, installer: request.Installer);

                if (decision == NetworkDecision.Blocked)
                {
                    _log.Record(request.Host, request.Port, decision, request.AiProviderId, installer: request.Installer);
                    await RefuseAsync(stream, request, "ILD network policy blocks " + request.Host + ".", stoppingToken).ConfigureAwait(false);
                    return;
                }

                // Resolved once, here: the addresses judged and dialled are the addresses resolved.
                IPAddress[] addresses;
                try
                {
                    addresses = await _resolve(request.Host, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ArgumentException)
                {
                    _log.Record(request.Host, request.Port, decision, request.AiProviderId, installer: request.Installer);
                    await WriteUnreachableAsync(stream, request, (ex as SocketException)?.SocketErrorCode.ToString() ?? "invalid host name", stoppingToken).ConfigureAwait(false);
                    return;
                }

                var isForward = request.Kind == RequestKind.Connect && snapshot.IsForward(request.Host, request.Port);
                if (snapshot.CheckAddresses(addresses, isForward) is { } refusal)
                {
                    _log.Record(request.Host, request.Port, NetworkDecision.Blocked, request.AiProviderId, refusal.Reason, request.Installer);
                    await RefuseAsync(stream, request, "ILD refuses " + request.Host + " (" + refusal.Reason + ").", stoppingToken).ConfigureAwait(false);
                    return;
                }

                _log.Record(request.Host, request.Port, decision, request.AiProviderId, installer: request.Installer);
                await RelayAsync(stream, request, addresses, head.Value, stoppingToken).ConfigureAwait(false);
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

    private async Task RelayAsync(NetworkStream client, ProxyRequest request, IPAddress[] addresses, ArraySegment<byte> head, CancellationToken stoppingToken)
    {
        TcpClient upstream;
        try
        {
            upstream = await _dial(addresses, request.Port, stoppingToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            await WriteUnreachableAsync(client, request, ex.SocketErrorCode.ToString(), stoppingToken).ConfigureAwait(false);
            return;
        }

        var upstreamStream = upstream.GetStream();
        var exchange = request.Kind == RequestKind.Http ? new PlainHttpExchange(request, head) : null;
        try
        {
            await _relay.RelayAsync(client, upstreamStream, request.Host, request.Port, request.AiProviderId, stoppingToken,
                token => SendHeadAsync(client, upstreamStream, request, head, token),
                exchange is null ? null : exchange.CopyRequestAsync,
                exchange is null ? null : exchange.CopyResponseAsync,
                request.Installer).ConfigureAwait(false);
        }
        finally
        {
            upstream.Close();
            upstream.Dispose();
        }
    }

    /// <summary>
    /// What each kind of request owes the two sides before bytes start flowing:
    /// the CONNECT acknowledgement plus whatever was read along with it, the
    /// ClientHello a redirected flow arrived with, or the rewritten origin-form
    /// head, whose body <see cref="PlainHttpExchange"/> sends on.
    /// </summary>
    private static async Task SendHeadAsync(NetworkStream client, NetworkStream upstream, ProxyRequest request, ArraySegment<byte> head, CancellationToken ct)
    {
        switch (request.Kind)
        {
            case RequestKind.Connect:
                await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct).ConfigureAwait(false);
                if (request.BodyStart < head.Count)
                    await upstream.WriteAsync(head.AsMemory(request.BodyStart), ct).ConfigureAwait(false);
                break;
            case RequestKind.Tls:
                await upstream.WriteAsync(head, ct).ConfigureAwait(false);
                break;
            default:
                await upstream.WriteAsync(request.ForwardedHead, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task WriteUnreachableAsync(NetworkStream client, ProxyRequest request, string reason, CancellationToken ct)
    {
        if (request.Kind != RequestKind.Tls)
            await WriteResponseAsync(client, "502 Bad Gateway", $"The egress proxy could not reach {request.Host}:{request.Port} ({reason}).", ct).ConfigureAwait(false);
    }

    private static async Task RefuseAsync(NetworkStream client, ProxyRequest request, string reason, CancellationToken ct)
    {
        if (request.Kind == RequestKind.Tls)
        {
            // access_denied alert: the only refusal a TLS client can read.
            await client.WriteAsync(new byte[] { 0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x31 }, ct).ConfigureAwait(false);
            return;
        }
        await WriteResponseAsync(client, "403 Forbidden", reason, ct).ConfigureAwait(false);
    }

    internal static async Task WriteResponseAsync(NetworkStream client, string status, string body, CancellationToken ct, string extraHeaders = "")
    {
        var payload = Encoding.UTF8.GetBytes(body + "\n");
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nProxy-Connection: close\r\n{extraHeaders}\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await client.WriteAsync(payload, ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read enough of the connection to know where it is going: a whole TLS
    /// record for a redirected flow, or through the blank line ending the HTTP
    /// head otherwise. Returns null on a closed or oversized head.
    /// </summary>
    private static async Task<ArraySegment<byte>?> ReadHeadAsync(NetworkStream stream, TimeSpan timeout, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        var buffer = new byte[InitialHeadBytes];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                if (buffer.Length >= MaxHeadBytes) return null;
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxHeadBytes));
            }
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                return null;
            }
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
    }

    internal static ProxyRequest? ParseRequest(ArraySegment<byte> head, string? hostOverride = null)
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
        string? credential = null;
        string? hostHeader = null;
        var forwarded = new StringBuilder();
        var sawHostHeader = false;
        var transferEncodings = new List<string>();
        var contentLengths = new List<string>();
        var asksUpgrade = false;

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                credential ??= value;
                continue;
            }
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostHeader = value;
                sawHostHeader = true;
                if (hostOverride is not null) continue;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                transferEncodings.Add(value);
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLengths.Add(value);
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                asksUpgrade = true;
            forwarded.Append(line).Append("\r\n");
        }

        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TrySplitHostPort(target, TlsDefaultPort, out var connectHost, out var connectPort)) return null;
            return new ProxyRequest(RequestKind.Connect, connectHost, connectPort, null, bodyStart, Array.Empty<byte>(), Credential: credential);
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

        if (!TryReadBodyFraming(transferEncodings, contentLengths, out var chunked, out var contentLength)) return null;

        // An absolute-form request may legitimately omit Host (the authority is in
        // the target); the origin-form request the upstream gets may not.
        var headText = new StringBuilder()
            .Append(method).Append(' ').Append(originTarget).Append(' ').Append(version).Append("\r\n")
            .Append(sawHostHeader && hostOverride is null ? string.Empty : "Host: " + (hostOverride ?? hostHeader) + "\r\n")
            .Append(forwarded)
            .Append("Connection: close\r\n\r\n")
            .ToString();
        return new ProxyRequest(RequestKind.Http, host, port, null, bodyStart, Encoding.ASCII.GetBytes(headText), chunked, contentLength, asksUpgrade, Credential: credential);
    }

    /// <summary>
    /// Where a plain request's body ends (RFC 9112 §6.3), so that nothing after it
    /// reaches the upstream. Framing two readers could disagree on — both headers,
    /// a last coding other than chunked, a Content-Length that is not one plain
    /// number — is refused rather than guessed at.
    /// </summary>
    private static bool TryReadBodyFraming(List<string> transferEncodings, List<string> contentLengths, out bool chunked, out long contentLength)
    {
        chunked = false;
        contentLength = 0;
        if (transferEncodings.Count > 0)
        {
            if (contentLengths.Count > 0) return false;
            var codings = string.Join(',', transferEncodings).Split(',');
            chunked = codings[^1].Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase);
            return chunked;
        }

        long? length = null;
        foreach (var value in contentLengths.SelectMany(v => v.Split(',')).Select(v => v.Trim()))
        {
            if (value.Length == 0 || !value.All(char.IsAsciiDigit) || !long.TryParse(value, out var parsed)) return false;
            if (length is not null && length != parsed) return false;
            length = parsed;
        }
        contentLength = length ?? 0;
        return true;
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

    /// <param name="ChunkedBody">A plain request's body is chunked; otherwise it is <paramref name="ContentLength"/> bytes long.</param>
    /// <param name="AsksUpgrade">A plain request names a protocol to switch to, the only case a 101 answers.</param>
    internal sealed record ProxyRequest(
        RequestKind Kind, string Host, int Port, Guid? AiProviderId, int BodyStart, byte[] ForwardedHead,
        bool ChunkedBody = false, long ContentLength = 0, bool AsksUpgrade = false, string? Installer = null, string? Credential = null);
}
