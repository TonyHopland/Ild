using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
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
