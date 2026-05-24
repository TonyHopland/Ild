using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using ILD.Api.Services;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests;

/// <summary>
/// Integration tests for <see cref="InteractiveProviderSessionService"/>.
/// Exercises the full path: real PTY allocation via Porta.Pty, real bytes
/// flowing through a paired in-memory WebSocket. Skipped on Windows because
/// the test relies on /bin/cat as a known-good interactive binary.
/// </summary>
public class InteractiveProviderSessionServiceTests
{
    private static bool IsUnix => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task BridgesBytesBetweenWebSocketAndPty()
    {
        if (!IsUnix) return; // PTY backend on Windows requires conhost; not the focus here.

        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance);

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Type = "cat",
            BaseUrl = "http://localhost",
            Model = "n/a",
            Config = "{\"binaryPath\":\"/bin/cat\"}",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = service.RunAsync(server, provider, initialCols: 80, initialRows: 24, cancellationToken: cts.Token);

        // /bin/cat in a PTY echoes input back when line buffering is enabled.
        var input = Encoding.UTF8.GetBytes("hello\n");
        await client.SendAsync(input, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

        var output = await ReadUntilAsync(client, "hello", TimeSpan.FromSeconds(5));
        Assert.Contains("hello", output);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await runTask;
    }

    [Fact]
    public async Task ClosesGracefullyWhenBinaryMissing()
    {
        if (!IsUnix) return;

        var (server, client) = WebSocketPair.Create();
        var service = new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance);

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "test",
            Type = "missing",
            BaseUrl = "http://localhost",
            Model = "n/a",
            Config = "{\"binaryPath\":\"/no/such/binary/here\"}",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = service.RunAsync(server, provider, initialCols: 80, initialRows: 24, cancellationToken: cts.Token);

        // Drain whatever the server sends and observe the close. With a real
        // PTY, a bogus binary path makes the child exec fail and the master
        // fd close immediately — what matters here is that the bridge tears
        // down without hanging.
        var buffer = new byte[1024];
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, cts.Token);
        }
        while (result.MessageType != WebSocketMessageType.Close);

        await runTask;
        Assert.True(true);
    }

    private static async Task<string> ReadUntilAsync(WebSocket socket, string needle, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        while (!cts.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            if (result.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (sb.ToString().Contains(needle, StringComparison.Ordinal)) break;
        }
        return sb.ToString();
    }
}
