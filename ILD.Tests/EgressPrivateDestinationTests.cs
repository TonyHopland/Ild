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
/// The private-address protection (ADR-0036) through the real proxy, the real policy and the real destination source together:
/// what the source builds is what the proxy refuses, and a declared forward is let through for CONNECT and plain HTTP alike.
/// </summary>
public sealed class EgressPrivateDestinationTests : IAsyncLifetime
{
    private const string Reason = "protected: Private and cluster-internal addresses";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;
    private readonly Dictionary<string, IPAddress> _dns = new()
    {
        ["db.internal.example"] = IPAddress.Parse("10.43.0.7"),
        ["lan.example"] = IPAddress.Parse("10.1.2.3"),
        ["public.example"] = IPAddress.Parse("203.0.113.9"),
        ["kubernetes.default.svc"] = IPAddress.Parse("10.96.0.1"),
    };
    private readonly List<(IPAddress[] Addresses, int Port)> _dials = new();
    private readonly List<string?> _reasons = new();
    private TcpListener? _upstream;
    private EgressPolicy? _policy;
    private EgressProxy? _proxy;
    private int _proxyPort;

    public EgressPrivateDestinationTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.OnOwnConnection());
        services.AddScoped<INetworkPolicyStore, NetworkPolicyStore>();
        services.AddScoped<INetworkForwardStore, NetworkForwardStore>();
        services.AddScoped<IAppSettingStore, AppSettingStore>();
        _services = services.BuildServiceProvider();
    }

    public ValueTask InitializeAsync()
    {
        _upstream = TcpListener.Create(0);
        _upstream.Start();
        _ = ServeAsync(_upstream);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_proxy is not null) await _proxy.StopAsync(CancellationToken.None);
        _upstream?.Stop();
        await _services.DisposeAsync();
        _db.Dispose();
    }

    private async Task StartAsync(RunHostKind kind)
    {
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        var source = new ProtectedDestinationSource(scopes,
            name => name == "ILD_DB_CONNECTION_STRING" ? "Host=db.internal.example;Database=ild" : null,
            kind, TimeProvider.System, resolve: ResolveAsync, ownAddresses: () => [IPAddress.Parse("10.42.0.5")],
            isolatedRanges: _ => Task.FromResult<IReadOnlyList<IpRange>>([IpRange.Parse("10.213.0.0/16")]));
        _policy = new EgressPolicy(scopes, TimeProvider.System, protectedSource: source);
        var options = new EgressProxyOptions(true, 0) { WebPort = ((IPEndPoint)_upstream!.LocalEndpoint).Port };
        _proxy = new EgressProxy(options, EgressProxyTests.Creds, _policy, new Log(_reasons), NullLogger<EgressProxy>.Instance, ResolveAsync, DialAsync);
        await _proxy.StartAsync(CancellationToken.None);
        _proxyPort = await _proxy.BoundPort.WaitAsync(Timeout);
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

    private static async Task ServeAsync(TcpListener listener)
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
                    if (read > 0 && Encoding.ASCII.GetString(buffer, 0, read).StartsWith("GET ", StringComparison.Ordinal))
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                }
            });
        }
    }

    private int DialCount { get { lock (_dials) return _dials.Count; } }

    private async Task SetModeAsync(NetworkMode mode)
    {
        await _db.Settings.UpsertAsync(AppSettingKeys.NetworkMode, EgressRules.ModeName(mode));
        _policy!.Invalidate();
    }

    private async Task ListAsync(string host)
    {
        await _db.Network.AddEntryAsync(new NetworkPolicyEntry { Host = host, ListKind = NetworkListKind.Whitelist });
        _policy!.Invalidate();
    }

    private async Task ForwardAsync(string host, int port)
    {
        await _db.NetworkForwards.AddForwardAsync(new NetworkForwardEntry
        {
            Id = Guid.NewGuid(), Name = "forward", Host = host, Port = port, LocalPort = 15432, CreatedAt = DateTime.UtcNow,
        });
        _policy!.Invalidate();
    }

    private async Task SetProtectionAsync(string id, bool enabled)
    {
        await _db.Settings.UpsertAsync(ProtectedDestination.SettingKey(id), enabled ? "true" : "false");
        _policy!.Invalidate();
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

    public static TheoryData<RunHostKind, string> Targets()
    {
        var data = new TheoryData<RunHostKind, string>();
        foreach (var kind in new[] { RunHostKind.Container, RunHostKind.Kubernetes })
            foreach (var target in new[]
            {
                "10.1.2.3", "10.255.255.255", "172.16.0.9", "172.31.255.1", "192.168.1.5", "100.64.0.1", "100.127.255.254",
                "[fc00::1]", "[fdff::1]", "[::ffff:192.168.1.5]", "[::ffff:10.1.2.3]", "lan.example",
            })
                data.Add(kind, target);
        return data;
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task A_private_address_is_refused_in_every_mode_until_forwarded_or_switched_off(RunHostKind kind, string target)
    {
        await StartAsync(kind);
        foreach (var mode in new[] { NetworkMode.Monitor, NetworkMode.Blacklist, NetworkMode.Whitelist })
        {
            await SetModeAsync(mode);
            if (mode == NetworkMode.Whitelist) await ListAsync(EgressRules.NormalizeHost(target));

            Assert.StartsWith("HTTP/1.1 403", await ConnectAsync(target, 5432));
            Assert.StartsWith("HTTP/1.1 403", await GetAsync(target, 5432));
        }
        Assert.Equal(0, DialCount);
        Assert.All(_reasons, r => Assert.Equal(Reason, r));
        Assert.Equal(6, _reasons.Count);

        // The host and port of a declared forward are let through, for CONNECT and for plain HTTP; another port is not.
        await ForwardAsync(EgressRules.NormalizeHost(target), 5432);
        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync(target, 5432));
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(target, 5432));
        Assert.Equal(2, DialCount);
        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync(target, 5433));
        Assert.StartsWith("HTTP/1.1 403", await GetAsync(target, 5433));
        Assert.Equal(2, DialCount);
    }

    [Theory]
    [InlineData(RunHostKind.Container, "192.168.1.5")]
    [InlineData(RunHostKind.Kubernetes, "10.1.2.3")]
    [InlineData(RunHostKind.Kubernetes, "[::ffff:10.1.2.3]")]
    public async Task With_the_switch_off_a_private_address_is_judged_by_the_mode_and_lists_alone(RunHostKind kind, string target)
    {
        await StartAsync(kind);
        await SetProtectionAsync(ProtectedDestination.Private, enabled: false);

        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync(target, 80));
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(target, 80));
        Assert.Equal(2, DialCount);
    }

    [Theory]
    [InlineData(RunHostKind.Container)]
    [InlineData(RunHostKind.Kubernetes)]
    public async Task A_forward_that_is_not_whitelisted_is_still_refused_by_the_lists(RunHostKind kind)
    {
        await StartAsync(kind);
        await ForwardAsync("lan.example", 80);
        await SetModeAsync(NetworkMode.Whitelist);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("lan.example", 80));
        Assert.StartsWith("HTTP/1.1 403", await GetAsync("lan.example", 80));
        Assert.Equal(0, DialCount);
    }

    [Theory]
    [InlineData(RunHostKind.Container)]
    [InlineData(RunHostKind.Kubernetes)]
    public async Task A_forward_to_loopback_is_still_refused_for_plain_http(RunHostKind kind)
    {
        await StartAsync(kind);
        await ForwardAsync("127.0.0.1", 8080);

        Assert.StartsWith("HTTP/1.1 403", await GetAsync("127.0.0.1", 8080));
        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("127.0.0.1", 8080));
        Assert.Equal(0, DialCount);
    }

    [Theory]
    [InlineData(RunHostKind.Container)]
    [InlineData(RunHostKind.Kubernetes)]
    public async Task The_existing_destinations_and_another_runs_network_keep_their_own_reasons(RunHostKind kind)
    {
        await StartAsync(kind);

        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("db.internal.example", 5432));
        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("10.42.0.5", 8080));
        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("169.254.169.254", 80));
        Assert.StartsWith("HTTP/1.1 403", await ConnectAsync("10.213.4.18", 3000));
        Assert.Equal(["protected: ILD database", "protected: ILD itself", "protected: Link-local and cloud metadata addresses", "another run's network"], _reasons);

        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync("public.example", 443));
        Assert.Equal(1, DialCount);
    }

    [Fact]
    public async Task A_run_host_on_this_machine_has_no_private_protection()
    {
        await StartAsync(RunHostKind.Local);

        Assert.StartsWith("HTTP/1.1 200", await ConnectAsync("192.168.1.5", 80));
        Assert.Equal(1, DialCount);
    }

    private sealed class Log(List<string?> reasons) : INetworkLogRecorder
    {
        public bool Record(string host, int port, NetworkDecision decision, Guid? aiProviderId, string? reason = null, string? installer = null)
        {
            if (decision == NetworkDecision.Blocked) lock (reasons) reasons.Add(reason);
            return true;
        }
    }
}
