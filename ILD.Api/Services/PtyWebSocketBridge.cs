using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Porta.Pty;

namespace ILD.Api.Services;

/// <summary>
/// Wire-protocol bridge between an HTTP WebSocket and a PTY-attached child
/// process. Callers supply the prepared <see cref="PtyOptions"/> (binary, cwd,
/// initial size, env) and own any setup/teardown around that — this helper
/// handles spawn, bidirectional byte pumping, resize control frames, and
/// graceful close.
///
/// Protocol:
///   Server -> client : binary frames, raw PTY output (UTF-8 + ANSI escapes).
///   Client -> server : binary frames = raw keystrokes piped to PTY input.
///                      text frames   = JSON control messages, currently
///                                      {"type":"resize","cols":N,"rows":M}.
/// </summary>
public static class PtyWebSocketBridge
{
    private const int ReadBufferSize = 8 * 1024;

    public static async Task RunAsync(
        WebSocket socket,
        PtyOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IPtyConnection? pty = null;
        try
        {
            try
            {
                pty = await PtyProvider.SpawnAsync(options, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to spawn PTY ({App}) in {Cwd}", options.App, options.Cwd);
                await SendErrorAndCloseAsync(
                    socket,
                    $"Failed to start '{options.App}': {ex.Message}",
                    cancellationToken);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pty.ProcessExited += (_, _) =>
            {
                try { linked.Cancel(); } catch { }
            };

            var ptyToSocket = PumpPtyToSocketAsync(pty, socket, linked.Token);
            var socketToPty = PumpSocketToPtyAsync(socket, pty, linked.Token);

            await Task.WhenAny(ptyToSocket, socketToPty);
            try { linked.Cancel(); } catch { }
            // The PTY reader stream on Unix doesn't observe the cancellation
            // token, so kill the child to unblock ReadAsync before draining.
            try { pty.Kill(); } catch { }

            try { await ptyToSocket; } catch { }
            try { await socketToPty; } catch { }

            await CloseSocketAsync(socket, WebSocketCloseStatus.NormalClosure, "session ended", cancellationToken);
        }
        finally
        {
            if (pty is not null)
            {
                try { pty.Kill(); } catch { }
                try { pty.Dispose(); } catch { }
            }
        }
    }

    public static async Task SendErrorAndCloseAsync(WebSocket socket, string message, CancellationToken ct)
    {
        try
        {
            var payload = Encoding.UTF8.GetBytes($"[ild] {message}\r\n");
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch { }
        await CloseSocketAsync(socket, WebSocketCloseStatus.InternalServerError, message, ct);
    }

    private static async Task PumpPtyToSocketAsync(IPtyConnection pty, WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                int read;
                try
                {
                    read = await pty.ReaderStream.ReadAsync(buffer.AsMemory(0, ReadBufferSize), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                if (read <= 0) break;

                await socket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, read),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken: ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpSocketToPtyAsync(WebSocket socket, IPtyConnection pty, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    try
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, 0, ReadBufferSize), ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (WebSocketException) { return; }

                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (ms.Length == 0) continue;
                var payload = ms.ToArray();

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleControlMessage(payload, pty);
                }
                else
                {
                    await pty.WriterStream.WriteAsync(payload, 0, payload.Length, ct);
                    await pty.WriterStream.FlushAsync(ct);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void HandleControlMessage(byte[] payload, IPtyConnection pty)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String) return;

            if (string.Equals(typeProp.GetString(), "resize", StringComparison.OrdinalIgnoreCase)
                && doc.RootElement.TryGetProperty("cols", out var colsProp)
                && doc.RootElement.TryGetProperty("rows", out var rowsProp)
                && colsProp.TryGetInt32(out var cols)
                && rowsProp.TryGetInt32(out var rows))
            {
                pty.Resize(Math.Clamp(cols, 20, 500), Math.Clamp(rows, 5, 200));
            }
        }
        catch
        {
            // Ignore malformed control frames.
        }
    }

    private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(status, description, ct);
            }
            catch { }
        }
    }
}
