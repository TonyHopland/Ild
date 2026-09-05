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

    public EgressProxyTests()
    {
        // Fresh contexts per scope: the proxy reads on its own threads, and a
        // DbContext must not be shared across them.
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Fresh());
        services.AddScoped<INetworkPolicyStore, NetworkPolicyStore>();
        services.AddScoped<IAppSettingStore, AppSettingStore>();
        _services = services.BuildServiceProvider();
        _policy = new EgressPolicy(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
    }

    public async Task InitializeAsync()
    {
        _upstream = new TcpListener(IPAddress.Loopback, 0);
        _upstream.Start();
        _upstreamPort = ((IPEndPoint)_upstream.LocalEndpoint).Port;
        _ = ServeUpstreamAsync(_upstream);

        _proxy = new EgressProxy(new EgressProxyOptions(true, 0), _policy, _log, NullLogger<EgressProxy>.Instance);
        await _proxy.StartAsync(CancellationToken.None);
        _proxyPort = await _proxy.BoundPort.WaitAsync(Timeout);
    }

    public async Task DisposeAsync()
    {
        if (_proxy is not null) await _proxy.StopAsync(CancellationToken.None);
        _upstream?.Stop();
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
        first.Dispose();
        Assert.StartsWith("HTTP/1.1 200", before);
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

    [Fact]
    public async Task A_plain_http_request_is_judged_by_its_host_and_forwarded_in_origin_form()
    {
        await SetModeAsync(NetworkMode.Whitelist);
        await ListAsync("localhost", NetworkListKind.Whitelist);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://localhost:{_upstreamPort}/things?x=1 HTTP/1.1\r\nHost: localhost:{_upstreamPort}\r\nProxy-Connection: keep-alive\r\n\r\n"));
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
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET http://localhost:{_upstreamPort}/ HTTP/1.1\r\nHost: localhost:{_upstreamPort}\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 403", await ReadToEndAsync(stream));
    }

    [Fact]
    public async Task A_redirected_tls_flow_is_judged_by_its_sni_and_refused_with_an_alert()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        await ListAsync(".blocked.example", NetworkListKind.Blacklist);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(TlsClientHelloTests.Build("api.blocked.example"));

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
            await stream.WriteAsync(Encoding.ASCII.GetBytes("last words"));
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
        Assert.Equal(NetworkMode.Blacklist, (await _policy.GetAsync()).Mode);

        // Written behind the cache's back: no Invalidate this time.
        await _db.Settings.UpsertAsync(AppSettingKeys.NetworkMode, "whitelist");
        Assert.Equal(NetworkMode.Blacklist, (await _policy.GetAsync()).Mode);

        _clock.Advance(EgressPolicy.CacheTtl + TimeSpan.FromMilliseconds(1));
        Assert.Equal(NetworkMode.Whitelist, (await _policy.GetAsync()).Mode);
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

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (notifier.Appended.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await recorder.StopAsync(CancellationToken.None);

        var log = await _db.Network.GetLogAsync(10);
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
        public Task PolicyChangedAsync() => Task.CompletedTask;
        public Task LogEntryAppendedAsync(NetworkLogEntry entry) { lock (Appended) Appended.Add(entry); return Task.CompletedTask; }
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
        var firstLoad = policy.GetAsync().AsTask();
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The edit lands while the first read is still in flight.
        store.Entries = new[] { Entry("new.example") };
        policy.Invalidate();
        store.Release();

        var snapshot = await firstLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("new.example", Assert.Single(snapshot.Entries).Host);
        Assert.Equal(2, store.Reads);
        Assert.Equal(NetworkDecision.Blocked, snapshot.Decide("new.example", null));
        Assert.Equal("new.example", Assert.Single((await policy.GetAsync()).Entries).Host);
        Assert.Equal(2, store.Reads);
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
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(head, "(?m)^Host: ").Count);
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

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(head, "(?m)^Host: ").Count);
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
