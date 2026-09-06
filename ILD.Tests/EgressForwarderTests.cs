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
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

/// <summary>
/// The forwarder end to end over loopback: real listeners, a real upstream and
/// the real database-backed policy, so "a client that cannot name its
/// destination still gets judged, logged by hostname, and cut off when the lists
/// change" is shown rather than asserted about a fake.
/// </summary>
public sealed class EgressForwarderTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TestDb _db = new();
    private readonly RecordingLog _log = new();
    private readonly CapturingLogger _diagnostics = new();
    private readonly ServiceProvider _services;
    private readonly EgressPolicy _policy;
    private EgressForwarder? _forwarder;
    private TcpListener? _upstream;
    private int _upstreamPort;

    public EgressForwarderTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Fresh());
        services.AddScoped<INetworkPolicyStore, NetworkPolicyStore>();
        services.AddScoped<INetworkForwardStore, NetworkForwardStore>();
        services.AddScoped<IAppSettingStore, AppSettingStore>();
        _services = services.BuildServiceProvider();
        _policy = new EgressPolicy(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
    }

    public async Task InitializeAsync()
    {
        _upstream = new TcpListener(IPAddress.Loopback, 0);
        _upstream.Start();
        _upstreamPort = ((IPEndPoint)_upstream.LocalEndpoint).Port;
        _ = EchoUpstreamAsync(_upstream);

        _forwarder = new EgressForwarder(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _policy,
            _log,
            new SilentNotifier(),
            _diagnostics);
        await _forwarder.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_forwarder is not null) await _forwarder.StopAsync(CancellationToken.None);
        _upstream?.Stop();
        await _services.DisposeAsync();
        _db.Dispose();
    }

    private static async Task EchoUpstreamAsync(TcpListener listener)
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
                        await stream.WriteAsync(buffer.AsMemory(0, read));
                }
            });
        }
    }

    /// <summary>A loopback port nothing is using, as far as the kernel just said.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

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

    /// <summary>Declare a forward to the echo upstream and wait until it is answering.</summary>
    private async Task<NetworkForwardEntry> DeclareAsync(string name = "echo", string host = "localhost", int? localPort = null)
    {
        var forward = new NetworkForwardEntry
        {
            Name = name,
            Host = host,
            Port = _upstreamPort,
            LocalPort = localPort ?? FreePort(),
        };
        await _db.NetworkForwards.AddForwardAsync(forward);
        _policy.Invalidate();
        await WaitUntilAsync(() => _forwarder!.ListeningPorts.Contains(forward.LocalPort));
        return forward;
    }

    private static async Task<TcpClient> DialAsync(int localPort)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort);
        return client;
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

    /// <summary>Reads until end-of-stream, which a refused or reset relay reaches at once.</summary>
    private static async Task AssertEndsPromptlyAsync(TcpClient client)
    {
        var buffer = new byte[16];
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            Assert.Equal(0, await client.GetStream().ReadAsync(buffer, cts.Token));
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task Off_mode_relays_through_the_forward_and_records_the_destination_by_hostname()
    {
        var forward = await DeclareAsync();

        using var client = await DialAsync(forward.LocalPort);
        Assert.Equal("ping through the forward", await EchoAsync(client, "ping through the forward"));

        Assert.Equal(("localhost", _upstreamPort, NetworkDecision.Advisory, (Guid?)null), Assert.Single(await RecordedAsync(1)));
    }

    /// <summary>
    /// The point of answering the connection rather than letting the kernel drop
    /// it: a client learns it cannot go there instead of timing out.
    /// </summary>
    [Fact]
    public async Task A_host_the_whitelist_does_not_cover_is_refused_promptly_and_logged_blocked()
    {
        await SetModeAsync(NetworkMode.Whitelist);
        var forward = await DeclareAsync();

        using var client = await DialAsync(forward.LocalPort);
        await AssertEndsPromptlyAsync(client);

        Assert.Equal(("localhost", _upstreamPort, NetworkDecision.Blocked, (Guid?)null), Assert.Single(await RecordedAsync(1)));
    }

    [Fact]
    public async Task Whitelisting_the_host_lets_the_next_connection_through_without_a_restart()
    {
        await SetModeAsync(NetworkMode.Whitelist);
        var forward = await DeclareAsync();

        using (var refused = await DialAsync(forward.LocalPort))
            await AssertEndsPromptlyAsync(refused);

        await ListAsync("localhost", NetworkListKind.Whitelist);

        using var allowed = await DialAsync(forward.LocalPort);
        Assert.Equal("now allowed", await EchoAsync(allowed, "now allowed"));
        Assert.Equal(
            new[] { NetworkDecision.Blocked, NetworkDecision.Allowed },
            (await RecordedAsync(2)).Select(e => e.Decision));
    }

    [Fact]
    public async Task Newly_blacklisting_the_host_resets_the_relay_already_open_through_the_forward()
    {
        await SetModeAsync(NetworkMode.Blacklist);
        var forward = await DeclareAsync();

        using (var client = await DialAsync(forward.LocalPort))
        {
            Assert.Equal("still open", await EchoAsync(client, "still open"));
            Assert.Equal(1, _forwarder!.OpenRelayCount);

            await ListAsync("localhost", NetworkListKind.Blacklist);

            await AssertEndsPromptlyAsync(client);
        }

        await WaitUntilAsync(() => _forwarder!.OpenRelayCount == 0);
        Assert.Contains(await RecordedAsync(2), e => e.Decision == NetworkDecision.Blocked && e.Host == "localhost");
    }

    [Fact]
    public async Task A_forward_deleted_at_runtime_stops_answering_on_its_local_port()
    {
        var forward = await DeclareAsync();

        Assert.True(await _db.NetworkForwards.DeleteForwardAsync(forward.Id));
        _policy.Invalidate();
        await WaitUntilAsync(() => !_forwarder!.ListeningPorts.Contains(forward.LocalPort));

        await Assert.ThrowsAnyAsync<SocketException>(async () => (await DialAsync(forward.LocalPort)).Dispose());
    }

    [Fact]
    public async Task A_local_port_already_in_use_is_reported_on_that_row_while_every_other_forward_keeps_serving()
    {
        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        try
        {
            var contested = new NetworkForwardEntry
            {
                Name = "contested",
                Host = "localhost",
                Port = _upstreamPort,
                LocalPort = ((IPEndPoint)squatter.LocalEndpoint).Port,
            };
            await _db.NetworkForwards.AddForwardAsync(contested);
            var healthy = await DeclareAsync(name: "healthy");

            await WaitUntilAsync(() => _forwarder!.ListenErrorFor(contested.Id) is not null);
            Assert.Contains("already in use", _forwarder!.ListenErrorFor(contested.Id));

            using var client = await DialAsync(healthy.LocalPort);
            Assert.Equal("unaffected", await EchoAsync(client, "unaffected"));
        }
        finally
        {
            squatter.Stop();
        }
    }

    /// <summary>
    /// Clients that reset before the accept completes are ordinary traffic, not a
    /// reason to stop serving; the listener has to survive a run of them.
    /// </summary>
    [Fact]
    public async Task Connections_abandoned_before_they_are_accepted_do_not_end_the_forward()
    {
        var forward = await DeclareAsync();

        for (var i = 0; i < 25; i++)
        {
            using var aborted = new TcpClient { LingerState = new LingerOption(true, 0) };
            await aborted.ConnectAsync(IPAddress.Loopback, forward.LocalPort);
        }

        using var client = await DialAsync(forward.LocalPort);
        Assert.Equal("still serving", await EchoAsync(client, "still serving"));
        Assert.Contains(forward.LocalPort, _forwarder!.ListeningPorts);
        Assert.Null(_forwarder.ListenErrorFor(forward.Id));
    }

    /// <summary>
    /// Only the caller's own failures are worth another accept. Retrying the rest
    /// spins a core on a listener that has stopped working while the row goes on
    /// saying it is listening, so they retire it and let the next reconcile
    /// rebind.
    /// </summary>
    [Theory]
    [InlineData(SocketError.ConnectionReset, true)]
    [InlineData(SocketError.ConnectionAborted, true)]
    [InlineData(SocketError.Interrupted, true)]
    [InlineData(SocketError.TooManyOpenSockets, false)]
    [InlineData(SocketError.NotSocket, false)]
    [InlineData(SocketError.InvalidArgument, false)]
    [InlineData(SocketError.NoBufferSpaceAvailable, false)]
    public void An_accept_failure_is_retried_only_when_it_was_the_clients(SocketError error, bool retried)
    {
        Assert.Equal(retried, EgressForwarder.IsAbandonedHandshake(error));
    }

    [Fact]
    public async Task One_forward_carries_several_connections_at_once()
    {
        var forward = await DeclareAsync();

        using var first = await DialAsync(forward.LocalPort);
        using var second = await DialAsync(forward.LocalPort);

        Assert.Equal("first", await EchoAsync(first, "first"));
        Assert.Equal("second", await EchoAsync(second, "second"));
        Assert.Equal(2, _forwarder!.OpenRelayCount);
    }

    /// <summary>
    /// The destinations recorded so far, once there are at least
    /// <paramref name="count"/> of them. A failure here reports what the forwarder
    /// logged, since the connection path swallows its own errors by design.
    /// </summary>
    private async Task<IReadOnlyList<(string Host, int Port, NetworkDecision Decision, Guid? Provider)>> RecordedAsync(int count)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (_log.Entries.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(_log.Entries.Count >= count,
            $"expected {count} recorded destination(s), saw {_log.Entries.Count}; forwarder said: {string.Join(" | ", _diagnostics.Lines)}");
        return _log.Entries;
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

    /// <summary>
    /// The forwarder swallows a failed connection into a debug line. Keeping those
    /// lines makes a failure here say what went wrong instead of only that nothing
    /// was recorded.
    /// </summary>
    private sealed class CapturingLogger : ILogger<EgressForwarder>
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : " -- " + exception)}");
        }
    }

    private sealed class SilentNotifier : INetworkNotifier
    {
        public Task PolicyChangedAsync() => Task.CompletedTask;
        public Task LogEntryAppendedAsync(NetworkLogEntry entry) => Task.CompletedTask;
        public Task LogClearedAsync() => Task.CompletedTask;
    }
}
