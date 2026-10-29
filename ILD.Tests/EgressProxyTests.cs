using System.Net;
using System.Net.Sockets;
using System.Text;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// The egress proxy end to end over loopback: a real listener, a real upstream,
/// and the real database-backed policy, so "an edit changes the outcome of the
/// agent's next connection without a restart" is shown rather than asserted
/// about a fake.
/// </summary>
public sealed class EgressProxyTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TestDb _db = new();
    private readonly RecordingLog _log = new();
    private readonly ServiceProvider _services;
    private readonly EgressPolicy _policy;
    private readonly FakeClock _clock = new();
    private EgressProxy? _proxy;
    private int _proxyPort;
    private TcpListener? _upstream;
    private int _upstreamPort;
    private readonly List<ScriptedUpstream> _scripted = new();
    private readonly List<EgressProxy> _extraProxies = new();

    public EgressProxyTests()
    {
        // A connection per scope: the proxy reads on its own threads, and neither
        // a DbContext nor a SQLite connection may be shared across them.
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.OnOwnConnection());
        services.AddScoped<INetworkPolicyStore, NetworkPolicyStore>();
        services.AddScoped<IAppSettingStore, AppSettingStore>();
        _services = services.BuildServiceProvider();
        _policy = new EgressPolicy(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
    }

    public async ValueTask InitializeAsync()
    {
        // Every scenario below that is not about the private-address block relays to
        // loopback, so the fixture allows it the way an operator would.
        await _db.Settings.UpsertAsync(AppSettingKeys.NetworkInternalAllow, "127.0.0.0/8\n::1");
        _policy.Invalidate();

        _upstream = new TcpListener(IPAddress.Loopback, 0);
        _upstream.Start();
        _upstreamPort = ((IPEndPoint)_upstream.LocalEndpoint).Port;
        _ = ServeUpstreamAsync(_upstream);

        _proxy = new EgressProxy(new EgressProxyOptions(true, 0), _policy, _log, NullLogger<EgressProxy>.Instance);
        await _proxy.StartAsync(CancellationToken.None);
        _proxyPort = await _proxy.BoundPort.WaitAsync(Timeout);
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.StopAsync(CancellationToken.None);
        foreach (var extra in _extraProxies) await extra.StopAsync(CancellationToken.None);
        _upstream?.Stop();
        foreach (var scripted in _scripted) scripted.Dispose();
        await _services.DisposeAsync();
        _db.Dispose();
    }

    /// <summary>
    /// Echoes raw bytes on a tunnel, and answers any plain HTTP request with a
    /// small 200 so the same listener serves CONNECT and plain-HTTP tests.
    /// </summary>
    private static async Task ServeUpstreamAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        var text = Encoding.ASCII.GetString(buffer, 0, read);
                        if (text.StartsWith("GET ", StringComparison.Ordinal))
                        {
                            var body = "hello from upstream";
                            var reply = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\nX-Saw: {text.Split("\r\n")[0]}\r\n\r\n{body}";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                            return;
                        }
                        await stream.WriteAsync(buffer.AsMemory(0, read));
                    }
                }
            });
        }
    }

    private async Task SetModeAsync(NetworkMode mode)
    {
        await _db.Settings.UpsertAsync(AppSettingKeys.NetworkMode, EgressRules.ModeName(mode));
        _policy.Invalidate();
    }

    private async Task<NetworkPolicyEntry> ListAsync(string host, NetworkListKind kind, Guid? provider = null)
    {
        var entry = new NetworkPolicyEntry { Host = host, ListKind = kind, AiProviderId = provider };
        await _db.Network.AddEntryAsync(entry);
        _policy.Invalidate();
        return entry;
    }

    private async Task<(TcpClient Client, string StatusLine)> ConnectAsync(string host, Guid? provider = null)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort);
        var stream = client.GetStream();
        var auth = provider is { } id
            ? $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"provider:{id}"))}\r\n"
            : string.Empty;
        var request = $"CONNECT {host}:{_upstreamPort} HTTP/1.1\r\nHost: {host}:{_upstreamPort}\r\n{auth}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var status = await ReadLineAsync(stream);
        while ((await ReadLineAsync(stream)).Length > 0) { }
        return (client, status);
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        using var cts = new CancellationTokenSource(Timeout);
        while (await stream.ReadAsync(one, cts.Token) == 1)
        {
            if (one[0] == '\n') break;
            if (one[0] != '\r') line.Append((char)one[0]);
        }
        return line.ToString();
    }

    private static async Task<string> ReadToEndAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cts.Token);
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task<string> EchoAsync(TcpClient client, string payload)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(payload));
        var buffer = new byte[payload.Length];
        var got = 0;
        using var cts = new CancellationTokenSource(Timeout);
        while (got < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(got), cts.Token);
            if (n == 0) break;
            got += n;
        }
        return Encoding.ASCII.GetString(buffer, 0, got);
    }

    [Fact]
    public async Task Off_mode_relays_every_connect_and_records_it_as_advisory()
    {
        var (client, status) = await ConnectAsync("localhost");
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 200", status);
            Assert.Equal("ping through the tunnel", await EchoAsync(client, "ping through the tunnel"));
        }

        var record = Assert.Single(_log.Entries);
        Assert.Equal(("localhost", _upstreamPort, NetworkDecision.Advisory, (Guid?)null), record);
    }

    [Fact]
    public async Task Blacklisting_a_host_denies_the_next_connection_without_a_restart()
    {
        await SetModeAsync(NetworkMode.Blacklist);

        var (first, before) = await ConnectAsync("localhost");
        Assert.StartsWith("HTTP/1.1 200", before);
        Assert.Equal("x", await EchoAsync(first, "x"));
        first.Dispose();
        await WaitUntilAsync(() => _proxy!.OpenTunnelCount == 0);

        await ListAsync("localhost", NetworkListKind.Blacklist);

        var (second, after) = await ConnectAsync("localhost");
        using (second)
        {
            Assert.StartsWith("HTTP/1.1 403", after);
            Assert.Contains("blocks localhost", await ReadToEndAsync(second.GetStream()));
        }

        Assert.Equal(
            new[] { NetworkDecision.Allowed, NetworkDecision.Blocked },
            _log.Entries.Select(e => e.Decision));
    }

    [Fact]
    public async Task Whitelisting_a_host_allows_the_next_connection_without_a_restart()
    {
        await SetModeAsync(NetworkMode.Whitelist);

        var (first, before) = await ConnectAsync("localhost");
        first.Dispose();
        Assert.StartsWith("HTTP/1.1 403", before);

        await ListAsync("localhost", NetworkListKind.Whitelist);

        var (second, after) = await ConnectAsync("localhost");
        using (second)
        {
            Assert.StartsWith("HTTP/1.1 200", after);
            Assert.Equal("now allowed", await EchoAsync(second, "now allowed"));
        }
    }

    [Fact]
    public async Task A_provider_scoped_whitelist_entry_admits_only_that_provider()
    {
        var mine = Guid.NewGuid();
        await _db.Providers.CreateAiProviderAsync(new AiProvider { Id = mine, Name = "mine", Type = "claude-code", BaseUrl = "", Model = "" });
        await SetModeAsync(NetworkMode.Whitelist);
        await ListAsync("localhost", NetworkListKind.Whitelist, mine);

        var (scoped, scopedStatus) = await ConnectAsync("localhost", mine);
        scoped.Dispose();
        var (other, otherStatus) = await ConnectAsync("localhost", Guid.NewGuid());
        other.Dispose();
        var (anonymous, anonymousStatus) = await ConnectAsync("localhost");
        anonymous.Dispose();

        Assert.StartsWith("HTTP/1.1 200", scopedStatus);
        Assert.StartsWith("HTTP/1.1 403", otherStatus);
        Assert.StartsWith("HTTP/1.1 403", anonymousStatus);
        Assert.Equal(mine, _log.Entries[0].Provider);
    }

    [Fact]
    public async Task Newly_blacklisting_a_host_resets_the_tunnel_already_open_to_it()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        var (client, status) = await ConnectAsync("localhost");
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 200", status);
            Assert.Equal("still open", await EchoAsync(client, "still open"));
            Assert.Equal(1, _proxy!.OpenTunnelCount);

            await ListAsync("localhost", NetworkListKind.Blacklist);

            // The proxy tears the relay down; the client sees end-of-stream (or a reset).
            var buffer = new byte[16];
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                Assert.Equal(0, await client.GetStream().ReadAsync(buffer, cts.Token));
            }
            catch (IOException) { }
        }

        await WaitUntilAsync(() => _proxy!.OpenTunnelCount == 0);
        Assert.Contains(_log.Entries, e => e.Decision == NetworkDecision.Blocked && e.Host == "localhost");
    }

    /// <summary>
    /// The tunnel must be open to policy no later than the moment the client is
    /// told the CONNECT succeeded — otherwise a blacklist landing in between
    /// leaves a live relay the re-judging sweep never sees.
    /// </summary>
    [Fact]
    public async Task A_tunnel_is_visible_to_policy_as_soon_as_the_client_sees_the_connect_succeed()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        var (client, status) = await ConnectAsync("localhost");
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 200", status);
            Assert.Equal(1, _proxy!.OpenTunnelCount);

            // No traffic driven first, so the blacklist lands on a tunnel that
            // has never relayed a byte.
            await ListAsync("localhost", NetworkListKind.Blacklist);

            var buffer = new byte[16];
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                Assert.Equal(0, await client.GetStream().ReadAsync(buffer, cts.Token));
            }
            catch (IOException) { }
        }

        await WaitUntilAsync(() => _proxy!.OpenTunnelCount == 0);
        Assert.Equal(
            new[] { NetworkDecision.Allowed, NetworkDecision.Blocked },
            _log.Entries.Select(e => e.Decision));
    }

    [Fact]
    public async Task A_plain_http_request_is_judged_by_its_host_and_forwarded_in_origin_form()
    {
        await SetModeAsync(NetworkMode.Whitelist);
        await ListAsync("localhost", NetworkListKind.Whitelist);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://localhost:{_upstreamPort}/things?x=1 HTTP/1.1\r\nHost: localhost:{_upstreamPort}\r\nProxy-Connection: keep-alive\r\n\r\n"), TestContext.Current.CancellationToken);
        var response = await ReadToEndAsync(stream);

        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains("X-Saw: GET /things?x=1 HTTP/1.1", response);
        Assert.EndsWith("hello from upstream", response);
        Assert.Equal(("localhost", _upstreamPort, NetworkDecision.Allowed, (Guid?)null), Assert.Single(_log.Entries));
    }

    [Fact]
    public async Task A_plain_http_request_to_a_blocked_host_gets_403()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        await ListAsync("localhost", NetworkListKind.Blacklist);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET http://localhost:{_upstreamPort}/ HTTP/1.1\r\nHost: localhost:{_upstreamPort}\r\n\r\n"), TestContext.Current.CancellationToken);

        Assert.StartsWith("HTTP/1.1 403", await ReadToEndAsync(stream));
    }

    [Fact]
    public async Task A_redirected_tls_flow_is_judged_by_its_sni_and_refused_with_an_alert()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        await ListAsync(".blocked.example", NetworkListKind.Blacklist);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(TlsClientHelloTests.Build("api.blocked.example"), TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource(Timeout);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cts.Token);

        Assert.Equal(new byte[] { 0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x31 }, ms.ToArray());
        Assert.Equal(("api.blocked.example", 443, NetworkDecision.Blocked, (Guid?)null), Assert.Single(_log.Entries));
    }

    [Fact]
    public async Task A_client_that_half_closes_after_its_request_still_gets_the_response()
    {
        var (client, status) = await ConnectAsync("localhost");
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 200", status);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("last words"), TestContext.Current.CancellationToken);
            client.Client.Shutdown(SocketShutdown.Send);

            // The upstream echo answers after it sees our EOF; the relay must still carry it back.
            Assert.Equal("last words", await ReadToEndAsync(stream));
        }
        await WaitUntilAsync(() => _proxy!.OpenTunnelCount == 0);
    }

    [Fact]
    public async Task The_cached_policy_expires_on_its_own_after_the_ttl()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        Assert.Equal(NetworkMode.Blacklist, (await _policy.GetAsync(TestContext.Current.CancellationToken)).Mode);

        // Written behind the cache's back: no Invalidate this time.
        await _db.Settings.UpsertAsync(AppSettingKeys.NetworkMode, "whitelist", TestContext.Current.CancellationToken);
        Assert.Equal(NetworkMode.Blacklist, (await _policy.GetAsync(TestContext.Current.CancellationToken)).Mode);

        _clock.Advance(EgressPolicy.CacheTtl + TimeSpan.FromMilliseconds(1));
        Assert.Equal(NetworkMode.Whitelist, (await _policy.GetAsync(TestContext.Current.CancellationToken)).Mode);
    }

    // Shaped like a Node/Express SSE reply whose handler sets Connection: keep-alive
    // itself, as LM Studio's does, whatever the request asked for.
    private const string SseHead =
        "HTTP/1.1 200 OK\r\nX-Powered-By: Express\r\nContent-Type: text/event-stream\r\nConnection: keep-alive\r\n"
        + "Keep-Alive: timeout=5\r\nDate: Mon, 19 Oct 2026 12:00:00 GMT\r\nTransfer-Encoding: chunked\r\n\r\n";

    private const string Stray = "GET http://localhost/stray HTTP/1.1\r\nHost: localhost\r\n\r\n";
    private const string ChunkedBody = "5;name=value\r\nhello\r\nA\r\n0123456789\r\n0\r\nX-Trailer: t\r\n\r\n";

    [Fact]
    public async Task A_streamed_keep_alive_response_reaches_the_client_chunk_by_chunk_saying_close_and_the_next_request_takes_a_new_connection()
    {
        const string request = "{\"stream\":true}";
        var first = Chunk("data: {\"delta\":\"Hel\"}\n\n");
        var second = Chunk("data: {\"delta\":\"lo\"}\n\n");
        const string last = "0\r\n\r\n";
        var clientHasFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync(request);
            await connection.SendAsync(SseHead + first);
            await clientHasFirst.Task.WaitAsync(Timeout);
            await connection.SendAsync(second + last);
            connection.EndResponse();
        });
        var provider = Guid.NewGuid();

        using (var client = await OpenAsync())
        {
            await SendAsync(client, PostHead(upstream.Port, $"Content-Length: {request.Length}", provider) + request);
            var soFar = await ReadUntilAsync(client.GetStream(), text => text.Contains(first, StringComparison.Ordinal));
            clientHasFirst.SetResult();
            var response = soFar + await ReadUntilClosedAsync(client.GetStream());

            var body = AssertClosingHead(response, "HTTP/1.1 200 OK",
                "X-Powered-By: Express", "Content-Type: text/event-stream", "Date: Mon, 19 Oct 2026 12:00:00 GMT", "Transfer-Encoding: chunked");
            Assert.Equal(first + second + last, body);
        }

        using (var again = await OpenAsync())
        {
            await SendAsync(again, PostHead(upstream.Port, $"Content-Length: {request.Length}", provider) + request);
            Assert.StartsWith("HTTP/1.1 200 OK\r\n", await ReadUntilClosedAsync(again.GetStream()));
        }

        Assert.Equal(2, upstream.Connections.Count);
        var row = ("localhost", upstream.Port, NetworkDecision.Advisory, (Guid?)provider);
        Assert.Equal(new[] { row, row }, _log.Entries);
        await WaitUntilAsync(() => _proxy!.OpenTunnelCount == 0);
    }

    [Fact]
    public async Task Headers_the_upstream_names_in_Connection_are_dropped_and_the_rest_kept_in_order()
    {
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n");
            await connection.SendAsync(
                "HTTP/1.1 200 OK\r\nX-Keep: 1\r\nConnection: keep-alive, X-Hop\r\nX-Hop: 1\r\nproxy-connection: keep-alive\r\n"
                + "CONNECTION: x-other\r\nX-Other: 2\r\nContent-Length: 2\r\nX-Hopper: 3\r\n\r\nok");
            connection.EndResponse();
        });

        using var client = await OpenAsync();
        await SendAsync(client, $"GET http://localhost:{upstream.Port}/ HTTP/1.1\r\nHost: localhost:{upstream.Port}\r\n\r\n");
        var response = await ReadUntilClosedAsync(client.GetStream());

        Assert.Equal("ok", AssertClosingHead(response, "HTTP/1.1 200 OK", "X-Keep: 1", "Content-Length: 2", "X-Hopper: 3"));
    }

    [Fact]
    public async Task Interim_responses_pass_unchanged_ahead_of_the_rewritten_final_head_and_a_body_sent_after_100_continue_arrives()
    {
        const string body = "{\"n\":1}";
        const string earlyHints = "HTTP/1.1 103 Early Hints\r\nLink: </style.css>; rel=preload\r\n\r\n";
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n");
            await connection.SendAsync("HTTP/1.1 100 Continue\r\n\r\n");
            await connection.WaitForAsync(body);
            await connection.SendAsync(earlyHints + "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok");
            connection.EndResponse();
        });

        using var client = await OpenAsync();
        var stream = client.GetStream();
        await SendAsync(client, PostHead(upstream.Port, $"Content-Length: {body.Length}\r\nExpect: 100-continue"));
        Assert.Equal("HTTP/1.1 100 Continue\r\n\r\n", await ReadUntilAsync(stream, text => text.Contains("\r\n\r\n", StringComparison.Ordinal)));

        await SendAsync(client, body);
        var rest = await ReadUntilClosedAsync(stream);
        Assert.StartsWith(earlyHints, rest);
        Assert.Equal("ok", AssertClosingHead(rest[earlyHints.Length..], "HTTP/1.1 200 OK", "Content-Length: 2"));

        client.Client.Shutdown(SocketShutdown.Send);
        var connection = Assert.Single(upstream.Connections);
        await connection.Closed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(body, BodyOf(connection.Received));
    }

    [Fact]
    public async Task A_switching_protocols_response_passes_unchanged_and_both_directions_then_relay_raw()
    {
        const string switching = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: echo\r\nConnection: Upgrade\r\n\r\n";
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n");
            await connection.SendAsync(switching);
            await connection.WaitForAsync("ping");
            await connection.SendAsync("pong");
            connection.EndResponse();
        });

        using var client = await OpenAsync();
        var stream = client.GetStream();
        await SendAsync(client, $"GET http://localhost:{upstream.Port}/socket HTTP/1.1\r\nHost: localhost:{upstream.Port}\r\nUpgrade: echo\r\n\r\n");
        Assert.Equal(switching, await ReadUntilAsync(stream, text => text.Contains("\r\n\r\n", StringComparison.Ordinal)));

        await SendAsync(client, "ping");
        Assert.Equal("pong", await ReadUntilClosedAsync(stream));
    }

    [Fact]
    public async Task A_switching_protocols_answer_to_a_request_that_asked_for_no_upgrade_gets_502_and_nothing_more_is_relayed()
    {
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n");
            await connection.SendAsync("HTTP/1.1 101 Switching Protocols\r\nUpgrade: echo\r\nConnection: Upgrade\r\n\r\n");
            await connection.Closed;
        });

        using var client = await OpenAsync();
        await SendAsync(client, PostHead(upstream.Port, "Content-Length: 0") + Stray);
        var response = await ReadUntilClosedAsync(client.GetStream());

        Assert.StartsWith("HTTP/1.1 502", response);
        var connection = Assert.Single(upstream.Connections);
        await connection.Closed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, BodyOf(connection.Received));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_request_on_the_same_connection_never_reaches_the_upstream(bool pipelined)
    {
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("{\"n\":1}");
            await connection.SendAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: keep-alive\r\nKeep-Alive: timeout=5\r\n\r\nok");
            connection.EndResponse();
        });
        var first = PostHead(upstream.Port, "Content-Length: 7") + "{\"n\":1}";
        var second = PostHead(upstream.Port, "Content-Length: 7") + "{\"n\":2}";

        using var client = await OpenAsync();
        string response;
        if (pipelined)
        {
            await SendAsync(client, first + second);
            response = await ReadUntilClosedAsync(client.GetStream());
            client.Client.Shutdown(SocketShutdown.Send);
        }
        else
        {
            await SendAsync(client, first);
            response = await ReadUntilClosedAsync(client.GetStream());
            // The proxy may already have hung up; what matters is where these bytes go.
            try
            {
                await SendAsync(client, second);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (IOException) { }
            catch (SocketException) { }
        }

        var connection = Assert.Single(upstream.Connections);
        await connection.Closed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
        Assert.EndsWith("\r\n\r\nok", response);

        var received = connection.Received;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(received, "^POST ", System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.DoesNotContain("Proxy-Authorization", received, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", received, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{\"n\":1}", BodyOf(received));
        Assert.Single(upstream.Connections);
    }

    [Theory]
    [InlineData("Content-Length: 11", "hello world", true, new[] { "hello world" + Stray })]
    [InlineData("Content-Length: 11", "hello world", false, new[] { "hello ", "world" + Stray })]
    [InlineData("Content-Length: 5, 5", "hello", true, new[] { "hello" + Stray })]
    [InlineData("Content-Length: 0", "", true, new[] { Stray })]
    [InlineData("", "", true, new[] { Stray })]
    [InlineData("Transfer-Encoding: chunked", ChunkedBody, true, new[] { ChunkedBody + Stray })]
    [InlineData("Transfer-Encoding: chunked", ChunkedBody, false, new[] { "5;name=val", "ue\r\nhel", "lo\r\nA\r\n0123456789\r", "\n0\r\nX-Trailer: t\r\n\r", "\n" + Stray })]
    [InlineData("Transfer-Encoding: gzip, chunked", "3\r\nabc\r\n0\r\n\r\n", true, new[] { "3\r\nabc\r\n0\r\n\r\n" + Stray })]
    public async Task A_request_body_reaches_the_upstream_whole_and_nothing_after_it_does(string framing, string body, bool withHead, string[] writes)
    {
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n" + body);
            await connection.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
            connection.EndResponse();
        });

        using var client = await OpenAsync();
        var head = PostHead(upstream.Port, framing);
        if (withHead)
        {
            await SendAsync(client, head + writes[0]);
        }
        else
        {
            await SendAsync(client, head);
            await WaitUntilAsync(() => upstream.Connections.Count == 1 && upstream.Connections[0].Received.Contains("\r\n\r\n", StringComparison.Ordinal));
            await SendAsync(client, writes[0]);
        }
        foreach (var write in writes.Skip(1))
            await SendAsync(client, write);

        var response = await ReadUntilClosedAsync(client.GetStream());
        client.Client.Shutdown(SocketShutdown.Send);
        var connection = Assert.Single(upstream.Connections);
        await connection.Closed.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
        Assert.Equal(body, BodyOf(connection.Received));
    }

    [Theory]
    [InlineData("Transfer-Encoding: chunked\r\nContent-Length: 5")]
    [InlineData("Transfer-Encoding: gzip")]
    [InlineData("Transfer-Encoding: chunked, gzip")]
    [InlineData("Content-Length: abc")]
    [InlineData("Content-Length: +5")]
    [InlineData("Content-Length: 5\r\nContent-Length: 6")]
    [InlineData("Content-Length: 5, 6")]
    public async Task A_request_whose_body_framing_is_ambiguous_gets_400_and_is_never_forwarded(string framing)
    {
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n");
            await connection.SendAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
            connection.EndResponse();
        });

        using var client = await OpenAsync();
        await SendAsync(client, PostHead(upstream.Port, framing) + "hello");

        Assert.StartsWith("HTTP/1.1 400", await ReadUntilClosedAsync(client.GetStream()));
        Assert.Empty(upstream.Connections);
    }

    [Fact]
    public async Task A_malformed_chunk_ends_the_connection_and_nothing_after_it_is_forwarded()
    {
        var upstream = StartUpstream(_ => Task.CompletedTask);

        using var client = await OpenAsync();
        await SendAsync(client, PostHead(upstream.Port, "Transfer-Encoding: chunked") + "5\r\nhello\r\n");
        await WaitUntilAsync(() => upstream.Connections.Count == 1 && upstream.Connections[0].Received.Contains("hello", StringComparison.Ordinal));
        await SendAsync(client, "ZZ\r\nmore data\r\n0\r\n\r\n");

        // No client EOF and no upstream reply: only the proxy can end this.
        await ReadUntilClosedAsync(client.GetStream());
        var connection = Assert.Single(upstream.Connections);
        await connection.Closed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("more data", connection.Received);
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.6\r\n\r\n")]
    [InlineData("HTTP/1.1 OK\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Le")]
    [InlineData("HTTP/1.1 200 OK\r\nX-Big: {big}\r\n\r\n")]
    public async Task A_malformed_truncated_or_oversized_upstream_head_gets_502_and_the_connection_closes(string reply)
    {
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n");
            await connection.SendAsync(reply.Replace("{big}", new string('a', 70 * 1024), StringComparison.Ordinal));
            connection.EndResponse();
        });

        using var client = await OpenAsync();
        await SendAsync(client, $"GET http://localhost:{upstream.Port}/ HTTP/1.1\r\nHost: localhost:{upstream.Port}\r\n\r\n");
        var response = await ReadUntilClosedAsync(client.GetStream());

        Assert.StartsWith("HTTP/1.1 502", response);
        var head = response[..response.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n");
        Assert.Contains(head, line => line.Equals("Connection: close", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Newly_blacklisting_a_host_resets_a_plain_http_response_still_streaming_from_it()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        var first = Chunk("data: {\"delta\":\"Hel\"}\n\n");
        var upstream = StartUpstream(async connection =>
        {
            await connection.WaitForAsync("\r\n\r\n");
            await connection.SendAsync(SseHead + first);
            await connection.Closed;
        });

        using (var client = await OpenAsync())
        {
            await SendAsync(client, $"GET http://localhost:{upstream.Port}/events HTTP/1.1\r\nHost: localhost:{upstream.Port}\r\n\r\n");
            await ReadUntilAsync(client.GetStream(), text => text.Contains(first, StringComparison.Ordinal));
            Assert.Equal(1, _proxy!.OpenTunnelCount);

            await ListAsync("localhost", NetworkListKind.Blacklist);

            await ReadUntilClosedAsync(client.GetStream());
        }

        await WaitUntilAsync(() => _proxy!.OpenTunnelCount == 0);
        Assert.Contains(_log.Entries, e => e.Decision == NetworkDecision.Blocked && e.Host == "localhost");
    }

    private ScriptedUpstream StartUpstream(Func<UpstreamConnection, Task> script)
    {
        var upstream = new ScriptedUpstream(script);
        _scripted.Add(upstream);
        return upstream;
    }

    private async Task<TcpClient> OpenAsync()
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort, TestContext.Current.CancellationToken);
        return client;
    }

    /// <summary>An absolute-form POST carrying proxy credentials, the way a proxied HTTP client sends it.</summary>
    private static string PostHead(int port, string framing, Guid? provider = null)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"provider:{provider ?? Guid.NewGuid()}"));
        return $"POST http://localhost:{port}/v1/chat/completions HTTP/1.1\r\nHost: localhost:{port}\r\n"
            + $"Proxy-Authorization: Basic {auth}\r\nContent-Type: application/json\r\n"
            + (framing.Length > 0 ? framing + "\r\n" : string.Empty) + "\r\n";
    }

    private static string Chunk(string data) => $"{data.Length:x}\r\n{data}\r\n";

    private static string BodyOf(string message) => message[(message.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];

    private static Task SendAsync(TcpClient client, string text)
        => client.GetStream().WriteAsync(Encoding.Latin1.GetBytes(text), TestContext.Current.CancellationToken).AsTask();

    private static async Task<string> ReadUntilAsync(NetworkStream stream, Func<string, bool> done)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(Timeout);
        while (!done(text.ToString()))
        {
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0) break;
            text.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
        return text.ToString();
    }

    /// <summary>Everything the proxy sends until it closes; a reset counts as closing.</summary>
    private static async Task<string> ReadUntilClosedAsync(NetworkStream stream)
    {
        var text = new StringBuilder();
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                text.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
        catch (IOException) { }
        return text.ToString();
    }

    /// <summary>
    /// Asserts the response head says close, with the status line and every
    /// end-to-end header as listed and in that order; returns the bytes after it.
    /// </summary>
    private static string AssertClosingHead(string response, string statusLine, params string[] endToEndHeaders)
    {
        var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(end >= 0, "no complete response head");
        var lines = response[..end].Split("\r\n");
        Assert.Equal(statusLine, lines[0]);

        string Name(string line) => line.IndexOf(':') is var colon and > 0 ? line[..colon] : line;
        string Value(string line) => line[(line.IndexOf(':') + 1)..].Trim();
        var hopByHop = new[] { "Connection", "Proxy-Connection", "Keep-Alive" };

        Assert.Equal(endToEndHeaders, lines.Skip(1).Where(l => !hopByHop.Contains(Name(l), StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("close", Value(Assert.Single(lines, l => Name(l).Equals("Connection", StringComparison.OrdinalIgnoreCase))), ignoreCase: true);
        Assert.Equal("close", Value(Assert.Single(lines, l => Name(l).Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))), ignoreCase: true);
        Assert.DoesNotContain(lines, l => Name(l).Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase));
        return response[(end + 4)..];
    }

    /// <summary>
    /// An upstream that records every byte each connection sends it, until that
    /// connection closes, while a per-test script decides what to answer.
    /// </summary>
    private sealed class ScriptedUpstream : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<UpstreamConnection> _connections = new();

        public ScriptedUpstream(Func<UpstreamConnection, Task> script)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync(script);
        }

        public int Port { get; }

        public IReadOnlyList<UpstreamConnection> Connections
        {
            get { lock (_connections) return _connections.ToList(); }
        }

        private async Task AcceptAsync(Func<UpstreamConnection, Task> script)
        {
            while (true)
            {
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                var connection = new UpstreamConnection(tcp);
                lock (_connections) _connections.Add(connection);
                _ = Task.Run(async () =>
                {
                    using (tcp)
                    {
                        var recording = connection.RecordUntilClosedAsync(_cts.Token);
                        try { await script(connection); }
                        catch (Exception) { }
                        await recording;
                    }
                });
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class UpstreamConnection
    {
        private readonly TcpClient _tcp;
        private readonly MemoryStream _received = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UpstreamConnection(TcpClient tcp) => _tcp = tcp;

        public string Received
        {
            get { lock (_received) return Encoding.Latin1.GetString(_received.ToArray()); }
        }

        /// <summary>Completes once the proxy has closed (or reset) its side.</summary>
        public Task Closed => _closed.Task;

        public Task WaitForAsync(string text) => WaitUntilAsync(() => Received.Contains(text, StringComparison.Ordinal));

        public Task SendAsync(string text) => _tcp.GetStream().WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

        /// <summary>
        /// Half-closes after a response, as a server does on Connection: close,
        /// while still recording whatever the proxy sends afterwards.
        /// </summary>
        public void EndResponse() => _tcp.Client.Shutdown(SocketShutdown.Send);

        public async Task RecordUntilClosedAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await _tcp.GetStream().ReadAsync(buffer, ct)) > 0)
                    lock (_received) _received.Write(buffer, 0, read);
            }
            catch (Exception) { }
            finally
            {
                _closed.TrySetResult();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(20);
        }
    }

    private sealed class CountingResolver
    {
        private readonly Func<string, IPAddress[]> _map;
        private int _calls;
        private readonly List<string> _hosts = new();

        public CountingResolver(Func<string, IPAddress[]> map) => _map = map;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string> Hosts { get { lock (_hosts) return _hosts.ToList(); } }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (_hosts) _hosts.Add(host);
            return Task.FromResult(_map(host));
        }
    }

    private async Task<int> StartProxyAsync(CountingResolver? resolver, EgressProxyOptions? options = null)
    {
        var proxy = new EgressProxy(options ?? new EgressProxyOptions(true, 0), _policy, _log, NullLogger<EgressProxy>.Instance,
            resolver is null ? null : resolver.ResolveAsync);
        await proxy.StartAsync(CancellationToken.None);
        _extraProxies.Add(proxy);
        return await proxy.BoundPort.WaitAsync(Timeout);
    }

    private async Task SetInternalAllowAsync(string value)
    {
        await _db.Settings.UpsertAsync(AppSettingKeys.NetworkInternalAllow, value);
        _policy.Invalidate();
    }

    private async Task<(TcpClient Client, string StatusLine)> ConnectViaAsync(int proxyPort, string host, int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"));
        var status = await ReadLineAsync(stream);
        while ((await ReadLineAsync(stream)).Length > 0) { }
        return (client, status);
    }

    private static readonly IPAddress[] Private10 = { IPAddress.Parse("10.1.2.3") };

    [Theory]
    [InlineData(NetworkMode.Off, false)]
    [InlineData(NetworkMode.Monitor, false)]
    [InlineData(NetworkMode.Whitelist, true)]
    [InlineData(NetworkMode.Blacklist, false)]
    public async Task A_name_that_resolves_to_a_private_address_is_refused_in_every_mode_even_when_listed(NetworkMode mode, bool whitelisted)
    {
        await SetInternalAllowAsync("");
        await SetModeAsync(mode);
        if (whitelisted) await ListAsync("intranet.example", NetworkListKind.Whitelist);
        var resolver = new CountingResolver(_ => Private10);
        var port = await StartProxyAsync(resolver);

        var (client, status) = await ConnectViaAsync(port, "intranet.example", 8443);
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 403", status);
            var body = await ReadToEndAsync(client.GetStream());
            Assert.Contains("intranet.example", body);
            Assert.Contains("private", body);
            Assert.Contains("Settings", body);
        }

        Assert.Equal(("intranet.example", 8443, NetworkDecision.Blocked, (Guid?)null), Assert.Single(_log.Entries));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("[::ffff:169.254.169.254]")]
    [InlineData("[::1]")]
    public async Task A_private_address_literal_is_refused(string literal)
    {
        await SetInternalAllowAsync("");
        var port = await StartProxyAsync(null);

        var (client, status) = await ConnectViaAsync(port, literal, _upstreamPort);
        client.Dispose();

        Assert.StartsWith("HTTP/1.1 403", status);
        Assert.Equal(NetworkDecision.Blocked, Assert.Single(_log.Entries).Decision);
    }

    [Fact]
    public async Task With_a_mixed_resolution_only_the_permitted_address_is_dialled_and_the_name_is_resolved_once()
    {
        // 127.0.0.0/8 is allowed by the fixture; 10.0.0.1 is not, and nothing listens there.
        var resolver = new CountingResolver(_ => new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Loopback });
        var port = await StartProxyAsync(resolver);

        var (client, status) = await ConnectViaAsync(port, "no-such-host.invalid", _upstreamPort);
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 200", status);
            Assert.Equal("through the seam", await EchoAsync(client, "through the seam"));
        }

        Assert.Equal(1, resolver.Calls);
        Assert.Equal(("no-such-host.invalid", _upstreamPort, NetworkDecision.Advisory, (Guid?)null), Assert.Single(_log.Entries));
    }

    [Fact]
    public async Task A_host_pattern_override_permits_the_names_it_matches_and_no_others()
    {
        await SetInternalAllowAsync(".lan.example");
        var resolver = new CountingResolver(_ => new[] { IPAddress.Loopback });
        var port = await StartProxyAsync(resolver);

        var (allowed, allowedStatus) = await ConnectViaAsync(port, "db.lan.example", _upstreamPort);
        using (allowed) Assert.StartsWith("HTTP/1.1 200", allowedStatus);
        var (other, otherStatus) = await ConnectViaAsync(port, "db.other.example", _upstreamPort);
        other.Dispose();

        Assert.StartsWith("HTTP/1.1 403", otherStatus);
    }

    [Fact]
    public async Task The_override_never_lifts_a_list_block_and_a_listed_out_host_is_not_resolved()
    {
        await SetInternalAllowAsync("127.0.0.0/8\n.lan.example");
        await SetModeAsync(NetworkMode.Blacklist);
        await ListAsync("db.lan.example", NetworkListKind.Blacklist);
        var resolver = new CountingResolver(_ => new[] { IPAddress.Loopback });
        var port = await StartProxyAsync(resolver);

        var (client, status) = await ConnectViaAsync(port, "db.lan.example", _upstreamPort);
        using (client)
        {
            Assert.StartsWith("HTTP/1.1 403", status);
            Assert.Contains("blocks db.lan.example", await ReadToEndAsync(client.GetStream()));
        }

        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task A_plain_http_request_to_a_private_host_gets_a_403_that_names_the_host()
    {
        await SetInternalAllowAsync("");
        var resolver = new CountingResolver(_ => Private10);
        var port = await StartProxyAsync(resolver);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET http://intranet.example:8080/x HTTP/1.1\r\nHost: intranet.example:8080\r\n\r\n"), TestContext.Current.CancellationToken);
        var response = await ReadToEndAsync(stream);

        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Contains("intranet.example", response);
        Assert.Equal(("intranet.example", 8080, NetworkDecision.Blocked, (Guid?)null), Assert.Single(_log.Entries));
    }

    [Fact]
    public async Task A_redirected_tls_flow_to_a_private_address_is_refused_with_the_alert()
    {
        await SetInternalAllowAsync("");
        var port = await StartProxyAsync(new CountingResolver(_ => Private10));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(TlsClientHelloTests.Build("intranet.example"), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(Timeout);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cts.Token);

        Assert.Equal(new byte[] { 0x15, 0x03, 0x01, 0x00, 0x02, 0x02, 0x31 }, ms.ToArray());
        Assert.Equal(("intranet.example", 443, NetworkDecision.Blocked, (Guid?)null), Assert.Single(_log.Entries));
    }

    [Fact]
    public async Task The_orchestrators_own_api_endpoint_is_exempt_on_its_exact_host_and_port_only()
    {
        await SetInternalAllowAsync("");
        var resolver = new CountingResolver(_ => new[] { IPAddress.Loopback });
        var port = await StartProxyAsync(resolver, new EgressProxyOptions(true, 0, ("ild.internal.test", _upstreamPort)));

        var (api, apiStatus) = await ConnectViaAsync(port, "ild.internal.test", _upstreamPort);
        using (api) Assert.StartsWith("HTTP/1.1 200", apiStatus);
        var (otherPort, otherPortStatus) = await ConnectViaAsync(port, "ild.internal.test", _upstreamPort + 1);
        otherPort.Dispose();
        var (otherHost, otherHostStatus) = await ConnectViaAsync(port, "elsewhere.internal.test", _upstreamPort);
        otherHost.Dispose();

        Assert.StartsWith("HTTP/1.1 403", otherPortStatus);
        Assert.StartsWith("HTTP/1.1 403", otherHostStatus);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_a_502_and_the_list_decision_is_still_recorded()
    {
        var port = await StartProxyAsync(new CountingResolver(_ => throw new SocketException((int)SocketError.HostNotFound)));

        var (client, status) = await ConnectViaAsync(port, "gone.example", 443);
        client.Dispose();

        Assert.StartsWith("HTTP/1.1 502", status);
        Assert.Equal(("gone.example", 443, NetworkDecision.Advisory, (Guid?)null), Assert.Single(_log.Entries));
    }

    private sealed class RecordingLog : INetworkLogRecorder
    {
        private readonly List<(string Host, int Port, NetworkDecision Decision, Guid? Provider)> _entries = new();

        public IReadOnlyList<(string Host, int Port, NetworkDecision Decision, Guid? Provider)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public void Record(string host, int port, NetworkDecision decision, Guid? aiProviderId)
        {
            lock (_entries) _entries.Add((host, port, decision, aiProviderId));
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan by)
            => Interlocked.Add(ref _timestamp, (long)(by.TotalSeconds * TimestampFrequency));
    }
}

/// <summary>
/// The recorder is the half of the log that touches the database: it batches
/// what the proxy reports, persists it with the hostname intact, and announces
/// each entry.
/// </summary>
public sealed class NetworkLogRecorderTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Persists_each_destination_with_its_hostname_and_announces_it()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INetworkPolicyStore>(_db.Network);
        using var provider = services.BuildServiceProvider();
        var notifier = new RecordingNotifier();
        var recorder = new NetworkLogRecorder(provider.GetRequiredService<IServiceScopeFactory>(), notifier, NullLogger<NetworkLogRecorder>.Instance);
        var providerId = Guid.NewGuid();

        await recorder.StartAsync(CancellationToken.None);
        recorder.Record("api.anthropic.com", 443, NetworkDecision.Allowed, providerId);
        recorder.Record("evil.example", 80, NetworkDecision.Blocked, null);

        await notifier.TwoAppended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await recorder.StopAsync(CancellationToken.None);

        var log = await _db.Network.GetLogAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(2, log.Count);
        var anthropic = Assert.Single(log, l => l.Host == "api.anthropic.com");
        Assert.Equal(443, anthropic.Port);
        Assert.Equal(NetworkDecision.Allowed, anthropic.Decision);
        Assert.Equal(providerId, anthropic.AiProviderId);
        Assert.Contains(notifier.Appended, e => e.Host == "evil.example" && e.Decision == NetworkDecision.Blocked);
    }

    private sealed class RecordingNotifier : INetworkNotifier
    {
        public List<NetworkLogEntry> Appended { get; } = new();
        public TaskCompletionSource TwoAppended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PolicyChangedAsync() => Task.CompletedTask;

        public Task LogEntryAppendedAsync(NetworkLogEntry entry)
        {
            lock (Appended)
            {
                Appended.Add(entry);
                if (Appended.Count >= 2) TwoAppended.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public Task LogClearedAsync() => Task.CompletedTask;
    }
}

/// <summary>
/// The cache must never publish lists that an edit has already replaced. The
/// race: a load starts, the operator's edit commits and invalidates, the load
/// finishes with the pre-edit rows. Kept, those rows would judge the next second
/// of connections and — worse — the tunnel re-check the edit triggered.
/// </summary>
public sealed class EgressPolicyInvalidationTests
{
    [Fact]
    public async Task A_load_overtaken_by_an_edit_is_discarded_and_redone()
    {
        var store = new GatedStore();
        var services = new ServiceCollection();
        services.AddSingleton<INetworkPolicyStore>(store);
        services.AddSingleton<IAppSettingStore>(new FixedSettings("blacklist"));
        using var provider = services.BuildServiceProvider();
        var policy = new EgressPolicy(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        store.Entries = new[] { Entry("old.example") };
        var firstLoad = policy.GetAsync(TestContext.Current.CancellationToken).AsTask();
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The edit lands while the first read is still in flight.
        store.Entries = new[] { Entry("new.example") };
        policy.Invalidate();
        store.Release();

        var snapshot = await firstLoad.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("new.example", Assert.Single(snapshot.Entries).Host);
        Assert.Equal(2, store.Reads);
        Assert.Equal(NetworkDecision.Blocked, snapshot.Decide("new.example", null));
        Assert.Equal("new.example", Assert.Single((await policy.GetAsync(TestContext.Current.CancellationToken)).Entries).Host);
        Assert.Equal(2, store.Reads);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("")]
    public async Task A_stored_mode_nobody_can_parse_is_judged_as_monitor_not_as_the_open_off(string stored)
    {
        var store = new GatedStore();
        store.Release();
        var services = new ServiceCollection();
        services.AddSingleton<INetworkPolicyStore>(store);
        services.AddSingleton<IAppSettingStore>(new FixedSettings(stored));
        using var provider = services.BuildServiceProvider();
        var policy = new EgressPolicy(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        var snapshot = await policy.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NetworkMode.Monitor, snapshot.Mode);
    }

    private static NetworkPolicyEntry Entry(string host)
        => new() { Id = Guid.NewGuid(), Host = host, ListKind = NetworkListKind.Blacklist };

    /// <summary>Returns the entries as they were when the read began; the first read blocks until released.</summary>
    private sealed class GatedStore : INetworkPolicyStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public IReadOnlyList<NetworkPolicyEntry> Entries { get; set; } = Array.Empty<NetworkPolicyEntry>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads => Volatile.Read(ref _reads);

        public void Release() => _gate.TrySetResult();

        public async Task<IReadOnlyList<NetworkPolicyEntry>> GetEntriesAsync(CancellationToken ct = default)
        {
            var seen = Entries;
            if (Interlocked.Increment(ref _reads) == 1)
            {
                Started.TrySetResult();
                await _gate.Task.WaitAsync(ct);
            }
            return seen;
        }

        public Task<NetworkPolicyEntry?> GetEntryAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetworkPolicyEntry?> FindEntryAsync(NetworkListKind kind, string host, Guid? aiProviderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddEntryAsync(NetworkPolicyEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteEntryAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NetworkLogEntry>> GetLogAsync(int take, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NetworkLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AppendLogAsync(IReadOnlyList<NetworkLogEntry> entries, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ClearLogAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> DeleteLogOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedSettings : IAppSettingStore
    {
        private readonly string _mode;
        public FixedSettings(string mode) { _mode = mode; }

        public Task<AppSetting?> GetByKeyAsync(string key, CancellationToken ct = default)
            => Task.FromResult<AppSetting?>(key == AppSettingKeys.NetworkMode ? new AppSetting { Key = key, Value = _mode } : null);
        public Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(string key, string value, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

public sealed class EgressProxyParseTests
{
    private static ArraySegment<byte> Head(string text) => new(Encoding.ASCII.GetBytes(text));

    private static string Forwarded(string request)
        => Encoding.ASCII.GetString(EgressProxy.ParseRequest(Head(request))!.ForwardedHead);

    [Fact]
    public void An_absolute_form_request_without_Host_gets_one_from_its_target()
    {
        var head = Forwarded("GET http://api.example.com:8080/v1/things?x=1 HTTP/1.1\r\nAccept: */*\r\n\r\n");

        Assert.StartsWith("GET /v1/things?x=1 HTTP/1.1\r\nHost: api.example.com:8080\r\n", head);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(head, "(?m)^Host: "));
        Assert.EndsWith("Connection: close\r\n\r\n", head);
    }

    [Fact]
    public void A_default_port_target_yields_a_bare_Host()
    {
        Assert.Contains("\r\nHost: api.example.com\r\n", Forwarded("GET http://api.example.com/ HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public void A_client_supplied_Host_is_kept_and_not_doubled()
    {
        var head = Forwarded("GET http://api.example.com/ HTTP/1.1\r\nHost: api.example.com\r\nProxy-Connection: keep-alive\r\n\r\n");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(head, "(?m)^Host: "));
        Assert.DoesNotContain("Proxy-Connection", head);
    }

    [Fact]
    public void Proxy_credentials_are_read_and_never_forwarded()
    {
        var id = Guid.NewGuid();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"provider:{id}"));
        var request = EgressProxy.ParseRequest(Head($"GET http://api.example.com/ HTTP/1.1\r\nProxy-Authorization: Basic {auth}\r\n\r\n"))!;

        Assert.Equal(id, request.AiProviderId);
        Assert.DoesNotContain("Proxy-Authorization", Encoding.ASCII.GetString(request.ForwardedHead));
    }
}
