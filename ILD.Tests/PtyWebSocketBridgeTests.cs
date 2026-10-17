using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using ILD.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Porta.Pty;

namespace ILD.Tests;

/// <summary>
/// The terminal bridge's wire protocol, driven over a real WebSocket pair (so a
/// normal close and an aborted socket look different) against a real PTY child.
/// </summary>
public class PtyWebSocketBridgeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_session_streams_output_takes_keystrokes_and_resizes_and_closes_normally_when_the_child_exits()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var cts = new CancellationTokenSource(Guard);
        var (server, client) = await ConnectedPairAsync(cts.Token);
        using var serverSocket = server;
        using var clientSocket = client;

        var options = new PtyOptions
        {
            Name = "ild-bridge-test",
            Cols = 80,
            Rows = 24,
            Cwd = Path.GetTempPath(),
            App = "/bin/sh",
            CommandLine = new[]
            {
                "-c",
                "printf 'size-before:%s\\n' \"$(stty size)\"; read line; printf 'got:%s\\n' \"$line\"; " +
                "printf 'size-after:%s\\n' \"$(stty size)\"; read done",
            },
            Environment = new Dictionary<string, string>(),
        };

        var run = PtyWebSocketBridge.RunAsync(server, options, NullLogger.Instance, cts.Token);

        var before = await ReadUntilAsync(client, "size-before:24 80", cts.Token);
        Assert.All(before.Types, t => Assert.Equal(WebSocketMessageType.Binary, t));

        await client.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"resize","cols":100,"rows":40}"""),
            WebSocketMessageType.Text, endOfMessage: true, cts.Token);
        await client.SendAsync(
            Encoding.UTF8.GetBytes("hello\n"), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);

        var after = await ReadUntilAsync(client, "size-after:40 100", cts.Token);
        Assert.Contains("got:hello", after.Text);
        Assert.All(after.Types, t => Assert.Equal(WebSocketMessageType.Binary, t));

        await client.SendAsync(
            Encoding.UTF8.GetBytes("bye\n"), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);

        var end = await ReadUntilCloseAsync(client, cts.Token);
        Assert.All(end.Types, t => Assert.Equal(WebSocketMessageType.Binary, t));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await run.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task A_child_that_cannot_be_spawned_is_reported_as_an_error_message_then_a_close()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var cts = new CancellationTokenSource(Guard);
        var (server, client) = await ConnectedPairAsync(cts.Token);
        using var serverSocket = server;
        using var clientSocket = client;

        var options = new PtyOptions
        {
            Name = "ild-bridge-test",
            Cols = 80,
            Rows = 24,
            Cwd = Path.GetTempPath(),
            // The one launch Porta.Pty refuses (1.x and 2.x alike): a missing or
            // non-executable binary still forks, and only the child fails.
            App = "",
            CommandLine = Array.Empty<string>(),
            Environment = new Dictionary<string, string>(),
        };

        var run = PtyWebSocketBridge.RunAsync(server, options, NullLogger.Instance, cts.Token);

        var received = await ReadUntilCloseAsync(client, cts.Token);
        Assert.Contains("Failed to start", received.Text);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, client.CloseStatus);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await run.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task The_exit_code_is_reported_even_when_a_grandchild_keeps_the_pty_open()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var cts = new CancellationTokenSource(Guard);
        var (server, client) = await ConnectedPairAsync(cts.Token);
        using var serverSocket = server;
        using var clientSocket = client;
        var pidFile = Path.Combine(Path.GetTempPath(), $"ild-bridge-grandchild-{Guid.NewGuid():N}");
        var clock = new HeldTimeProvider();

        var options = new PtyOptions
        {
            Name = "ild-bridge-test",
            Cols = 80,
            Rows = 24,
            Cwd = Path.GetTempPath(),
            App = "/bin/sh",
            // The background sleep inherits the PTY and ignores the SIGHUP the shell's
            // exit sends it, so the output never reaches EOF and only the bridge's
            // bounded wait ends it.
            CommandLine = new[] { "-c", $"echo ready; read line; (trap \"\" HUP; exec sleep 60) & echo $! > '{pidFile}'; echo boom; exit 3" },
            Environment = new Dictionary<string, string>(),
        };

        try
        {
            var run = PtyWebSocketBridge.RunAsync(server, options, NullLogger.Instance, cts.Token, reportExit: true, timeProvider: clock);

            await ReadUntilAsync(client, "ready", cts.Token);
            await client.SendAsync(
                Encoding.UTF8.GetBytes("go\n"), WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
            var output = await ReadUntilAsync(client, "boom", cts.Token);
            await clock.WaitForTimerAsync(cts.Token);
            clock.Release();
            var rest = await ReadUntilCloseAsync(client, cts.Token);

            Assert.Contains("exited with code 3", output.Text + rest.Text);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);

            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            await run.WaitAsync(cts.Token);
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                if (int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                {
                    try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch (ArgumentException) { }
                }
                File.Delete(pidFile);
            }
        }
    }

    /// <summary>
    /// The child closes its side of the PTY (EOF) and exits, but Porta.Pty's watcher
    /// thread reports the exit only after the output drain's bound has passed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_exit_code_is_reported_when_the_exit_event_comes_after_the_output_has_ended(bool endsInEio)
    {
        using var cts = new CancellationTokenSource(Guard);
        var (server, client) = await ConnectedPairAsync(cts.Token);
        using var serverSocket = server;
        using var clientSocket = client;
        var pty = new ScriptedPty("boom\r\n", endsInEio);
        var clock = new SteppedTimeProvider();

        var run = PtyWebSocketBridge.RunAsync(
            server, ScriptedPty.Options, NullLogger.Instance, cts.Token,
            reportExit: true, timeProvider: clock, spawn: (_, _) => Task.FromResult<IPtyConnection>(pty));

        var output = await ReadUntilAsync(client, "boom", cts.Token);
        // The output has ended; the bounded drain wait runs out with no exit seen yet.
        await clock.FireNextAsync(cts.Token);
        // Whatever the bridge arms next is what it waits on now; the exit arrives during it.
        await clock.NextArmedAsync(cts.Token);
        pty.Exit(3);
        var rest = await ReadUntilCloseAsync(client, cts.Token);

        Assert.Contains("[ild] The CLI exited with code 3.", output.Text + rest.Text);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await run.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task A_session_whose_exit_event_never_comes_still_closes_normally_without_an_exit_line()
    {
        using var cts = new CancellationTokenSource(Guard);
        var (server, client) = await ConnectedPairAsync(cts.Token);
        using var serverSocket = server;
        using var clientSocket = client;
        var pty = new ScriptedPty("boom\r\n");
        var clock = new SteppedTimeProvider();

        var run = PtyWebSocketBridge.RunAsync(
            server, ScriptedPty.Options, NullLogger.Instance, cts.Token,
            reportExit: true, timeProvider: clock, spawn: (_, _) => Task.FromResult<IPtyConnection>(pty));

        var output = await ReadUntilAsync(client, "boom", cts.Token);
        // Every bound the bridge sets runs out, one after the other, until it closes.
        var closing = ReadUntilCloseAsync(client, cts.Token);
        while (!closing.IsCompleted)
        {
            var fired = clock.FireNextAsync(cts.Token);
            await Task.WhenAny(fired, closing);
        }
        var rest = await closing;

        Assert.DoesNotContain("exited with code", output.Text + rest.Text);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await run.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task A_client_that_leaves_while_the_exit_event_is_awaited_is_not_kept_waiting()
    {
        using var cts = new CancellationTokenSource(Guard);
        var (server, client) = await ConnectedPairAsync(cts.Token);
        using var serverSocket = server;
        using var clientSocket = client;
        var pty = new ScriptedPty("boom\r\n", endsInEio: true);
        var clock = new SteppedTimeProvider();

        var run = PtyWebSocketBridge.RunAsync(
            server, ScriptedPty.Options, NullLogger.Instance, cts.Token,
            reportExit: true, timeProvider: clock, spawn: (_, _) => Task.FromResult<IPtyConnection>(pty));

        await ReadUntilAsync(client, "boom", cts.Token);
        await clock.FireNextAsync(cts.Token);
        await clock.NextArmedAsync(cts.Token);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);

        // No timer fires from here on: the bridge ends because the client left.
        await run.WaitAsync(cts.Token);
    }

    [Fact]
    public async Task The_exit_code_is_reported_when_the_child_exited_before_the_bridge_was_listening()
    {
        using var cts = new CancellationTokenSource(Guard);
        var (server, client) = await ConnectedPairAsync(cts.Token);
        using var serverSocket = server;
        using var clientSocket = client;
        var pty = new ScriptedPty("boom\r\n");
        pty.ExitUnseen(3);
        var clock = new SteppedTimeProvider();

        var run = PtyWebSocketBridge.RunAsync(
            server, ScriptedPty.Options, NullLogger.Instance, cts.Token,
            reportExit: true, timeProvider: clock, spawn: (_, _) => Task.FromResult<IPtyConnection>(pty));

        var closing = ReadUntilCloseAsync(client, cts.Token);
        while (!closing.IsCompleted)
        {
            var fired = clock.FireNextAsync(cts.Token);
            await Task.WhenAny(fired, closing);
        }
        var output = await closing;

        Assert.Contains("boom", output.Text);
        Assert.Contains("[ild] The CLI exited with code 3.", output.Text);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);

        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await run.WaitAsync(cts.Token);
    }

    /// <summary>
    /// A PTY child that has written <c>output</c> and closed its side: the reader gives
    /// that, then EOF, or with <c>endsInEio</c> the EIO a read gives on Linux. Its exit is reported only when the test calls <see cref="Exit"/>,
    /// the way Porta.Pty 2.2.2 does it: <c>ProcessExited</c> is raised from its own
    /// waitpid thread with the exit code in <see cref="PtyExitedEventArgs"/>, apart from
    /// the reader reaching EOF.
    /// </summary>
    private sealed class ScriptedPty(string output, bool endsInEio = false) : IPtyConnection
    {
        public static readonly PtyOptions Options = new()
        {
            Name = "ild-bridge-test",
            Cols = 80,
            Rows = 24,
            Cwd = Path.GetTempPath(),
            App = "/bin/false",
            CommandLine = Array.Empty<string>(),
            Environment = new Dictionary<string, string>(),
        };

        private int _exitCode;
        private volatile bool _exited;

        public Stream ReaderStream { get; } = endsInEio
            ? new EioAtEndStream(Encoding.UTF8.GetBytes(output))
            : new MemoryStream(Encoding.UTF8.GetBytes(output), writable: false);
        public Stream WriterStream { get; } = Stream.Null;
        public bool SupportsCancellableRead => false;
        public int Pid => 4242;
        public int ExitCode => _exitCode;
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

        public void Exit(int code)
        {
            ExitUnseen(code);
            // Porta.Pty constructs these itself; its constructor is internal.
            var args = (PtyExitedEventArgs)Activator.CreateInstance(
                typeof(PtyExitedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, [code], null)!;
            ProcessExited?.Invoke(this, args);
        }

        /// <summary>
        /// The child exits while nobody is subscribed yet: Porta.Pty raises
        /// <c>ProcessExited</c> to no one, and only <see cref="WaitForExit"/> tells.
        /// </summary>
        public void ExitUnseen(int code)
        {
            _exitCode = code;
            _exited = true;
        }

        public void Kill() { }
        public void Resize(int cols, int rows) { }
        public bool WaitForExit(int milliseconds) => _exited;
        public void Dispose() { }

        private sealed class EioAtEndStream(byte[] output) : MemoryStream(output, writable: false)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await base.ReadAsync(buffer, cancellationToken);
                return read > 0 ? read : throw new IOException("Input/output error");
            }
        }
    }

    /// <summary>
    /// A clock whose timers fire only when the test fires them, one at a time and in
    /// the order they were armed.
    /// </summary>
    private sealed class SteppedTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly Queue<SteppedTimer> _armed = new();
        private TaskCompletionSource _anyArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;
        private int _consumed;

        /// <summary>Completes once a timer this method has not reported yet is armed.</summary>
        public async Task NextArmedAsync(CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_seen > _consumed)
                    {
                        _consumed++;
                        return;
                    }
                    wait = _anyArmed.Task;
                }
                await wait.WaitAsync(ct);
            }
        }

        /// <summary>Waits for the oldest armed timer that has not fired, and fires it.</summary>
        public async Task FireNextAsync(CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    while (_armed.TryDequeue(out var timer))
                    {
                        if (timer.Disposed) continue;
                        if (_consumed < _seen) _consumed++;
                        timer.Fire();
                        return;
                    }
                    wait = _anyArmed.Task;
                }
                await wait.WaitAsync(ct);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SteppedTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        private void Arm(SteppedTimer timer)
        {
            TaskCompletionSource armed;
            lock (_gate)
            {
                _armed.Enqueue(timer);
                _seen++;
                armed = _anyArmed;
                _anyArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            armed.TrySetResult();
        }

        private sealed class SteppedTimer(SteppedTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            public volatile bool Disposed;

            public void Fire() => ThreadPool.QueueUserWorkItem(_ => { if (!Disposed) callback(state); });

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan) clock.Arm(this);
                return true;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A clock whose timers never fire until <see cref="Release"/>, and fire as soon
    /// as they are armed after it.
    /// </summary>
    private sealed class HeldTimeProvider : TimeProvider
    {
        private readonly List<HeldTimer> _armed = new();
        private readonly TaskCompletionSource _timerArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _released;

        public Task WaitForTimerAsync(CancellationToken ct) => _timerArmed.Task.WaitAsync(ct);

        public void Release()
        {
            List<HeldTimer> due;
            lock (_armed)
            {
                _released = true;
                due = _armed.ToList();
                _armed.Clear();
            }
            foreach (var timer in due) timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new HeldTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        private void Arm(HeldTimer timer)
        {
            lock (_armed)
            {
                if (!_released)
                {
                    _armed.Add(timer);
                    _timerArmed.TrySetResult();
                    return;
                }
            }
            timer.Fire();
        }

        private sealed class HeldTimer(HeldTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private volatile bool _disposed;

            public void Fire()
            {
                if (!_disposed) ThreadPool.QueueUserWorkItem(_ => { if (!_disposed) callback(state); });
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan) clock.Arm(this);
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record Received(string Text, IReadOnlyList<WebSocketMessageType> Types);

    private static async Task<Received> ReadUntilAsync(WebSocket socket, string needle, CancellationToken ct)
    {
        var text = new StringBuilder();
        var types = new List<WebSocketMessageType>();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(needle, StringComparison.Ordinal))
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            Assert.True(result.MessageType != WebSocketMessageType.Close, $"closed before '{needle}' arrived; got: {text}");
            types.Add(result.MessageType);
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        return new Received(text.ToString(), types);
    }

    private static async Task<Received> ReadUntilCloseAsync(WebSocket socket, CancellationToken ct)
    {
        var text = new StringBuilder();
        var types = new List<WebSocketMessageType>();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return new Received(text.ToString(), types);
            types.Add(result.MessageType);
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
    }

    private static async Task<(WebSocket Server, WebSocket Client)> ConnectedPairAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var accept = listener.AcceptSocketAsync(ct).AsTask();
            await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, ct);
            var serverSocket = await accept;

            var server = WebSocket.CreateFromStream(
                new NetworkStream(serverSocket, ownsSocket: true),
                new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });
            var client = WebSocket.CreateFromStream(
                new NetworkStream(clientSocket, ownsSocket: true),
                new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
            return (server, client);
        }
        finally
        {
            listener.Stop();
        }
    }
}
