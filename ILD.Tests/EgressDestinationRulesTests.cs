using System.Net;
using System.Net.Sockets;
using System.Text;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// What the egress proxy refuses by the addresses it resolved (ADR-0036): loopback always, ILD's own
/// infrastructure while its protection is on, and neither a LAN address nor a forward to ILD's database.
/// </summary>
public sealed class EgressDestinationRulesTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Dictionary<string, IPAddress> Infrastructure = new()
    {
        ["ild.internal.example"] = IPAddress.Parse("198.51.100.1"),
        ["db.internal.example"] = IPAddress.Parse("198.51.100.2"),
        ["workitems.internal.example"] = IPAddress.Parse("198.51.100.3"),
        ["kubernetes.default.svc"] = IPAddress.Parse("198.51.100.4"),
    };

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;
    private readonly EgressPolicy _policy;
    private readonly RecordingLog _log = new();
    private readonly Dictionary<string, IPAddress> _dns = new(Infrastructure)
    {
        ["lan.example"] = IPAddress.Parse("192.168.1.20"),
        ["localhost"] = IPAddress.Loopback,
        ["sneaky.example"] = IPAddress.Loopback,
    };
    private readonly List<(IPAddress[] Addresses, int Port)> _dials = new();
    private readonly List<string> _upstreamHeads = new();
    private TcpListener? _upstream;
    private EgressProxy? _proxy;
    private int _proxyPort;

    public EgressDestinationRulesTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.OnOwnConnection());
        services.AddScoped<INetworkPolicyStore, NetworkPolicyStore>();
        services.AddScoped<INetworkForwardStore, NetworkForwardStore>();
        services.AddScoped<IAppSettingStore, AppSettingStore>();
        _services = services.BuildServiceProvider();
        _policy = new EgressPolicy(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, protectedSource: new FixedSource());
    }

    public async ValueTask InitializeAsync()
    {
        _upstream = TcpListener.Create(0);
        _upstream.Start();
        _ = ServeAsync(_upstream);

        var options = new EgressProxyOptions(true, 0) { PreviewsEnabled = true, WebPort = ((IPEndPoint)_upstream.LocalEndpoint).Port };
        _proxy = new EgressProxy(options, EgressProxyTests.Creds, _policy, _log, NullLogger<EgressProxy>.Instance, ResolveAsync, DialAsync);
        await _proxy.StartAsync(CancellationToken.None);
        _proxyPort = await _proxy.BoundPort.WaitAsync(Timeout);
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.StopAsync(CancellationToken.None);
        _upstream?.Stop();
        await _services.DisposeAsync();
        _db.Dispose();
    }

    private Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        => Task.FromResult(IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? new[] { literal }
            : new[] { _dns.TryGetValue(host, out var address) ? address : IPAddress.Parse("203.0.113.9") });

    private async Task<TcpClient> DialAsync(IPAddress[] addresses, int port, CancellationToken ct)
    {
        lock (_dials) _dials.Add((addresses, port));
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_upstream!.LocalEndpoint).Port, ct);
        return client;
    }

    private async Task ServeAsync(TcpListener listener)
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
                    var read = await stream.ReadAsync(buffer);
                    if (read > 0)
                    {
                        var text = Encoding.ASCII.GetString(buffer, 0, read);
                        lock (_upstreamHeads) _upstreamHeads.Add(text);
                        if (text.StartsWith("GET ", StringComparison.Ordinal))
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                    }
                }
            });
        }
    }

    private int DialCount { get { lock (_dials) return _dials.Count; } }

    private async Task SetModeAsync(NetworkMode mode)
    {
        await _db.Settings.UpsertAsync(AppSettingKeys.NetworkMode, EgressRules.ModeName(mode));
        _policy.Invalidate();
    }

    private async Task ListAsync(string host, NetworkListKind kind)
    {
        await _db.Network.AddEntryAsync(new NetworkPolicyEntry { Host = host, ListKind = kind });
        _policy.Invalidate();
    }

    private async Task ForwardAsync(string host, int port)
    {
        await _db.NetworkForwards.AddForwardAsync(new NetworkForwardEntry
        {
            Id = Guid.NewGuid(), Name = "forward", Host = host, Port = port, LocalPort = 15432, CreatedAt = DateTime.UtcNow,
        });
        _policy.Invalidate();
    }

    private async Task SetProtectionAsync(string id, bool enabled)
    {
        await _db.Settings.UpsertAsync(ProtectedDestination.SettingKey(id), enabled ? "true" : "false");
        _policy.Invalidate();
    }

    private async Task<string> SendAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxyPort);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(EgressProxyTests.Authed(request)));
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private Task<string> ConnectAsync(string host, int port)
        => SendAsync($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");

    private Task<string> GetAsync(string host, int port)
        => SendAsync($"GET http://{host}:{port}/ HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");

    public static TheoryData<string> LoopbackTargets => new()
    {
        "127.0.0.1", "127.8.9.10", "[::1]", "[::ffff:127.0.0.1]", "0.0.0.0", "[::]", "localhost", "sneaky.example",
    };

    [Theory]
    [MemberData(nameof(LoopbackTargets))]
    public async Task Loopback_is_refused_in_every_mode_even_whitelisted_and_never_dialled(string target)
    {
        foreach (var mode in new[] { NetworkMode.Monitor, NetworkMode.Blacklist, NetworkMode.Whitelist })
        {
            await SetModeAsync(mode);
            if (mode == NetworkMode.Whitelist) await ListAsync(EgressRules.NormalizeHost(target), NetworkListKind.Whitelist);

            Assert.StartsWith("HTTP/1.1 403", await ConnectAsync(target, 8080));
            Assert.StartsWith("HTTP/1.1 403", await GetAsync(target, 8080));
        }

        Assert.Equal(0, DialCount);
        Assert.All(_log.Reasons, r => Assert.Equal("loopback", r));
        Assert.Equal(6, _log.Reasons.Count);
    }

    [Fact]
    public async Task A_forward_to_loopback_is_refused_too()
    {
        await ForwardAsync("localhost", 5432);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("localhost", 5432));
        Assert.Equal(0, DialCount);
    }

    [Theory]
    [InlineData("ild.internal.example", ProtectedDestination.Ild, "protected: ILD itself")]
    [InlineData("db.internal.example", ProtectedDestination.Database, "protected: ILD database")]
    [InlineData("workitems.internal.example", ProtectedDestination.WorkItemServer, "protected: WorkItem Server")]
    [InlineData("kubernetes.default.svc", ProtectedDestination.KubernetesApi, "protected: Kubernetes API")]
    [InlineData("198.51.100.4", ProtectedDestination.KubernetesApi, "protected: Kubernetes API")]
    public async Task A_protected_destination_is_refused_even_whitelisted_until_its_protection_is_switched_off(string host, string id, string reason)
    {
        await SetModeAsync(NetworkMode.Whitelist);
        await ListAsync(host, NetworkListKind.Whitelist);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync(host, 5432));
        Assert.StartsWith("HTTP/1.1 403", await GetAsync(host, 80));
        Assert.Equal(0, DialCount);
        Assert.All(_log.Reasons, r => Assert.Equal(reason, r));
        Assert.Equal([NetworkDecision.Blocked, NetworkDecision.Blocked], _log.Entries.Select(e => e.Decision));

        await SetProtectionAsync(id, enabled: false);

        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync(host, 5432));
        Assert.Equal(1, DialCount);
    }

    [Fact]
    public async Task A_protection_switched_off_hands_the_destination_back_to_the_lists()
    {
        await SetProtectionAsync(ProtectedDestination.Database, enabled: false);
        await SetModeAsync(NetworkMode.Whitelist);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("db.internal.example", 5432));
        Assert.Equal(0, DialCount);
        Assert.Null(Assert.Single(_log.Reasons));
    }

    [Theory]
    [InlineData(NetworkMode.Monitor, false)]
    [InlineData(NetworkMode.Whitelist, true)]
    public async Task A_declared_forward_to_the_database_is_judged_by_the_lists_and_not_the_protection(NetworkMode mode, bool listed)
    {
        await ForwardAsync("db.internal.example", 5432);
        await SetModeAsync(mode);
        if (listed) await ListAsync("db.internal.example", NetworkListKind.Whitelist);

        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync("db.internal.example", 5432));
        Assert.Equal(1, DialCount);
        Assert.Null(Assert.Single(_log.Reasons));
    }

    [Fact]
    public async Task A_declared_forward_not_on_the_whitelist_is_refused_by_the_lists()
    {
        await ForwardAsync("db.internal.example", 5432);
        await SetModeAsync(NetworkMode.Whitelist);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("db.internal.example", 5432));
        Assert.Equal(0, DialCount);
    }

    [Fact]
    public async Task Another_port_on_the_forwarded_host_is_still_protected()
    {
        await ForwardAsync("db.internal.example", 5432);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("db.internal.example", 5433));
        Assert.StartsWith("HTTP/1.1 403", await GetAsync("db.internal.example", 5432));
        Assert.Equal(0, DialCount);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("[fd00:ec2::254]")]
    [InlineData("[fe80::1]")]
    [InlineData("169.254.1.1")]
    public async Task Link_local_and_cloud_metadata_addresses_are_refused_by_default_and_handed_back_when_switched_off(string host)
    {
        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync(host, 80));
        Assert.StartsWith("HTTP/1.1 403", await GetAsync(host, 80));
        Assert.Equal(0, DialCount);
        Assert.Equal("protected: Link-local and cloud metadata addresses", _log.Reasons.Last());

        await SetProtectionAsync(ProtectedDestination.LinkLocal, false);
        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync(host, 80));
        Assert.Equal(1, DialCount);
    }

    [Theory]
    [InlineData("10.213.4.18")]
    [InlineData("10.213.255.254")]
    public async Task Another_runs_network_is_refused_even_when_forwarded_or_every_protection_is_off(string host)
    {
        foreach (var id in ProtectedDestination.AllIds) await SetProtectionAsync(id, false);
        await ForwardAsync(host, 3000);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync(host, 3000));
        Assert.StartsWith("HTTP/1.1 403", await GetAsync(host, 3000));
        Assert.Equal(0, DialCount);
        Assert.Equal("another run's network", _log.Reasons.Last());
        // Just outside the pool is judged like any other address.
        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync("10.214.0.1", 3000));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("lan.example")]
    public async Task A_lan_address_is_judged_by_the_mode_and_lists_alone(string host)
    {
        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync(host, 80));
        Assert.Equal(1, DialCount);

        await SetModeAsync(NetworkMode.Whitelist);
        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync(host, 80));
        Assert.Equal(1, DialCount);

        await ListAsync(host, NetworkListKind.Whitelist);
        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync(host, 80));
        Assert.Equal(2, DialCount);
    }

    [Fact]
    public async Task A_preview_request_reaches_ild_with_the_preview_host_whatever_host_header_the_client_sent()
    {
        var response = await SendAsync("GET http://wi-12.preview.ild/page HTTP/1.1\r\nHost: localhost:9999\r\nX-Other: 1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200", response);
        var head = Assert.Single(_upstreamHeads);
        Assert.Contains("\r\nHost: wi-12.preview.ild\r\n", head);
        Assert.DoesNotContain("localhost", head);
        Assert.Contains("X-Other: 1", head);
    }

    [Theory]
    [InlineData(RunHostKind.Local, "127.0.0.1")]
    [InlineData(RunHostKind.Container, "0.0.0.0")]
    [InlineData(RunHostKind.Kubernetes, "0.0.0.0")]
    public void The_bind_address_follows_the_run_host_kind(RunHostKind kind, string expected)
        => Assert.Equal(IPAddress.Parse(expected), EgressProxyOptions.BindAddressFor(kind));

    private sealed class FixedSource : IProtectedDestinationSource
    {
        public ValueTask<IReadOnlyList<ProtectedDestination>> GetAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ProtectedDestination>>(
            [
                new(ProtectedDestination.Ild, "ILD itself", ["ild.internal.example"], [Infrastructure["ild.internal.example"]]),
                new(ProtectedDestination.Database, "ILD database", ["db.internal.example"], [Infrastructure["db.internal.example"]]),
                new(ProtectedDestination.WorkItemServer, "WorkItem Server", ["workitems.internal.example"], [Infrastructure["workitems.internal.example"]]),
                new(ProtectedDestination.KubernetesApi, "Kubernetes API", ["kubernetes.default.svc"], [Infrastructure["kubernetes.default.svc"]]),
                ProtectedDestination.LinkLocalRanges,
            ]);

        public ValueTask<IReadOnlyList<IpRange>> GetIsolatedRangesAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<IpRange>>([IpRange.Parse("10.213.0.0/16")]);
    }

    private sealed class RecordingLog : INetworkLogRecorder
    {
        private readonly List<(string Host, int Port, NetworkDecision Decision, string? Reason)> _entries = new();

        public IReadOnlyList<(string Host, int Port, NetworkDecision Decision)> Entries
        {
            get { lock (_entries) return _entries.Select(e => (e.Host, e.Port, e.Decision)).ToList(); }
        }

        public IReadOnlyList<string?> Reasons
        {
            get { lock (_entries) return _entries.Select(e => e.Reason).ToList(); }
        }

        public void Record(string host, int port, NetworkDecision decision, Guid? aiProviderId, string? reason = null, string? installer = null)
        {
            lock (_entries) _entries.Add((host, port, decision, reason));
        }
    }
}
