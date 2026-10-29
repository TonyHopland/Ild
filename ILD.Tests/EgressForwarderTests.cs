using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using ILD.Tests.Fakes;
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
    private readonly RecordingLogger<EgressForwarder> _diagnostics = new();
    private readonly ServiceProvider _services;
    private readonly EgressPolicy _policy;
    private EgressForwarder? _forwarder;
    private TcpListener? _upstream;
    private int _upstreamPort;

    public EgressForwarderTests()
    {
        var services = new ServiceCollection();
        // A connection per scope: a policy edit sets the relay re-judging and the
        // listeners reconciling at once, each reading on a thread of its own.
        services.AddScoped(_ => _db.OnOwnConnection());
        services.AddScoped<INetworkPolicyStore, NetworkPolicyStore>();
        services.AddScoped<INetworkForwardStore, NetworkForwardStore>();
        services.AddScoped<IAppSettingStore, AppSettingStore>();
        _services = services.BuildServiceProvider();
        _policy = new EgressPolicy(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
    }

    public async ValueTask InitializeAsync()
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

    public async ValueTask DisposeAsync()
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

    // Below the ephemeral range, which the kernel hands to every other test's
    // listeners and outgoing connections: a port probed free there can be taken
    // before the forwarder binds it, which is what an ephemeral probe allowed.
    private const int FirstForwardPort = 20000;
    private const int ForwardPortCount = 10000;
    private static int _nextForwardPort = Random.Shared.Next(ForwardPortCount);

    /// <summary>A loopback port nothing is using, never handed out twice in this process.</summary>
    private static int FreePort()
    {
        while (true)
        {
            var port = FirstForwardPort + (int)((uint)Interlocked.Increment(ref _nextForwardPort) % ForwardPortCount);
            var probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
            }
            catch (SocketException)
            {
                continue;
            }
            probe.Stop();
            return port;
        }
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

    /// <summary>
    /// Declare a forward to the echo upstream and bring the listeners in line with it.
    /// The reconcile runs here instead of being prompted by a policy change, so the
    /// listener is known to be bound before the test dials it.
    /// </summary>
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
        await _forwarder!.ReconcileAsync(default);
        Assert.Contains(forward.LocalPort, _forwarder.ListeningPorts);
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

    /// <summary>
    /// Once released, the port is the kernel's to hand to anyone, so whether a
    /// dial to it is refused says nothing about the forwarder. What the forwarder
    /// owes is closing its own listening socket, so that is what gets checked.
    /// </summary>
    [Fact]
    public async Task A_forward_deleted_at_runtime_stops_answering_on_its_local_port()
    {
        // Read once, at a listener's first accept, which runs inside the bind
        // right after Start: after Stop, TcpListener.Server hands out a fresh socket.
        var bound = new ConcurrentDictionary<TcpListener, Socket>();
        _forwarder!.AcceptClient = (listener, ct) =>
        {
            bound.GetOrAdd(listener, l => l.Server);
            return listener.AcceptTcpClientAsync(ct);
        };
        var forward = await DeclareAsync();

        using (var client = await DialAsync(forward.LocalPort))
            Assert.Equal("before delete", await EchoAsync(client, "before delete"));
        Assert.Contains(bound.Values, socket => ((IPEndPoint)socket.LocalEndPoint!).Port == forward.LocalPort);
        Assert.All(bound.Values, socket => Assert.False(socket.SafeHandle.IsClosed));

        Assert.True(await _db.NetworkForwards.DeleteForwardAsync(forward.Id, TestContext.Current.CancellationToken));
        await _forwarder.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(forward.LocalPort, _forwarder.ListeningPorts);

        Assert.All(bound.Values, socket => Assert.True(socket.SafeHandle.IsClosed));
    }

    [Fact]
    public async Task A_forward_moved_to_another_local_port_stops_answering_on_the_old_one_and_answers_on_the_new_one()
    {
        var bound = new ConcurrentDictionary<TcpListener, (Socket Socket, int Port)>();
        _forwarder!.AcceptClient = (listener, ct) =>
        {
            bound.GetOrAdd(listener, l => (l.Server, ((IPEndPoint)l.LocalEndpoint).Port));
            return listener.AcceptTcpClientAsync(ct);
        };
        var forward = await DeclareAsync();
        var oldPort = forward.LocalPort;
        using (var first = await DialAsync(oldPort))
            Assert.Equal("on the old port", await EchoAsync(first, "on the old port"));
        var newPort = FreePort();

        await _db.NetworkForwards.UpdateForwardAsync(forward.Id, forward.Name, forward.Host, forward.Port, newPort, TestContext.Current.CancellationToken);
        await _forwarder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(oldPort, _forwarder.ListeningPorts);
        Assert.Contains(newPort, _forwarder.ListeningPorts);
        Assert.Contains(bound.Values, b => b.Port == oldPort && b.Socket.SafeHandle.IsClosed);
        using var client = await DialAsync(newPort);
        Assert.Equal("on the new port", await EchoAsync(client, "on the new port"));
    }

    [Fact]
    public async Task A_renamed_forward_keeps_its_listener_and_the_connection_open_through_it()
    {
        var bound = new ConcurrentDictionary<TcpListener, Socket>();
        _forwarder!.AcceptClient = (listener, ct) =>
        {
            bound.GetOrAdd(listener, l => l.Server);
            return listener.AcceptTcpClientAsync(ct);
        };
        var forward = await DeclareAsync();
        using var open = await DialAsync(forward.LocalPort);
        Assert.Equal("before", await EchoAsync(open, "before"));

        await _db.NetworkForwards.UpdateForwardAsync(forward.Id, "renamed", forward.Host, forward.Port, forward.LocalPort, TestContext.Current.CancellationToken);
        await _forwarder.ReconcileAsync(TestContext.Current.CancellationToken);

        Assert.All(bound.Values, socket => Assert.False(socket.SafeHandle.IsClosed));
        Assert.Equal("after", await EchoAsync(open, "after"));
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
            await _db.NetworkForwards.AddForwardAsync(contested, TestContext.Current.CancellationToken);
            var healthy = await DeclareAsync(name: "healthy");

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
    /// reason to stop serving; the listener has to survive a run of them. The
    /// accept is where the kernel reports them, so that is where they are staged.
    /// </summary>
    [Fact]
    public async Task Connections_abandoned_before_they_are_accepted_do_not_end_the_forward()
    {
        var abandoned = 3;
        _forwarder!.AcceptClient = (socket, ct) => Interlocked.Decrement(ref abandoned) >= 0
            ? ValueTask.FromException<TcpClient>(new SocketException((int)SocketError.ConnectionReset))
            : socket.AcceptTcpClientAsync(ct);
        var forward = await DeclareAsync();

        using var client = await DialAsync(forward.LocalPort);
        Assert.Equal("still serving", await EchoAsync(client, "still serving"));
        Assert.True(Volatile.Read(ref abandoned) < 0, "the abandoned accepts were never staged");
        Assert.Contains(forward.LocalPort, _forwarder.ListeningPorts);
        Assert.Null(_forwarder.ListenErrorFor(forward.Id));
    }

    /// <summary>
    /// Binding and retiring race the accept loop that each bind starts, and a
    /// loop that faults on the way up leaves a row advertising a port nothing
    /// answers on. Withdraw a port and declare it again, and insist that
    /// "listening" keeps meaning it.
    /// </summary>
    [Fact]
    public async Task A_port_withdrawn_and_declared_again_still_answers_when_it_says_it_is_listening()
    {
        var localPort = FreePort();

        var first = await DeclareAsync(name: "first", localPort: localPort);
        using (var client = await DialAsync(localPort))
            Assert.Equal("round trip", await EchoAsync(client, "round trip"));

        Assert.True(await _db.NetworkForwards.DeleteForwardAsync(first.Id, TestContext.Current.CancellationToken));
        await _forwarder!.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(localPort, _forwarder.ListeningPorts);

        await DeclareAsync(name: "again", localPort: localPort);
        using (var client = await DialAsync(localPort))
            Assert.Equal("round trip", await EchoAsync(client, "round trip"));
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
        try
        {
            await _log.AtLeast(count).WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"expected {count} recorded destination(s), saw {_log.Entries.Count}; forwarder said: {string.Join(" | ", _diagnostics.Entries)}");
        }
        return _log.Entries;
    }

    // Only for what the kernel reports with no event to await: a relay's count
    // falls when the socket it carried is closed.
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
        private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = new();

        public IReadOnlyList<(string Host, int Port, NetworkDecision Decision, Guid? Provider)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public void Record(string host, int port, NetworkDecision decision, Guid? aiProviderId)
        {
            lock (_entries)
            {
                _entries.Add((host, port, decision, aiProviderId));
                foreach (var waiter in _waiters.Where(w => w.Count <= _entries.Count).ToList())
                {
                    waiter.Tcs.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> destinations have been recorded.</summary>
        public Task AtLeast(int count)
        {
            lock (_entries)
            {
                if (_entries.Count >= count) return Task.CompletedTask;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, tcs));
                return tcs.Task;
            }
        }
    }

    /// <summary>
    /// The forwarder swallows a failed connection into a debug line. Keeping those
    /// lines makes a failure here say what went wrong instead of only that nothing
    /// was recorded.
    /// </summary>

    private sealed class SilentNotifier : INetworkNotifier
    {
        public Task PolicyChangedAsync() => Task.CompletedTask;
        public Task LogEntryAppendedAsync(NetworkLogEntry entry) => Task.CompletedTask;
        public Task LogClearedAsync() => Task.CompletedTask;
    }
}
