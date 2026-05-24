using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ILD.Data.Entities;
using Porta.Pty;

namespace ILD.Api.Services;

/// <summary>
/// Bridges an HTTP WebSocket and a PTY-attached child process so a user can
/// drive an AI provider's interactive TUI from the browser.
///
/// Protocol:
///   Server -> client : binary frames, raw PTY output (UTF-8 + ANSI escapes).
///   Client -> server : binary frames = raw keystrokes piped to PTY input.
///                      text frames   = JSON control messages, currently
///                                      {"type":"resize","cols":N,"rows":M}.
/// Session is tab-bound — closing the socket kills the PTY and removes the
/// scratch working directory.
/// </summary>
public sealed class InteractiveProviderSessionService
{
    private const int ReadBufferSize = 8 * 1024;
    private readonly ILogger<InteractiveProviderSessionService> _logger;

    public InteractiveProviderSessionService(ILogger<InteractiveProviderSessionService> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        WebSocket socket,
        AiProvider provider,
        int initialCols,
        int initialRows,
        CancellationToken cancellationToken)
    {
        var binaryPath = ResolveBinaryPath(provider);
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            await SendErrorAndCloseAsync(socket, "Provider has no binaryPath configured.", cancellationToken);
            return;
        }

        var sessionId = Guid.NewGuid();
        var sessionRoot = Path.Combine(Path.GetTempPath(), "ild-sessions", sessionId.ToString("N"));
        Directory.CreateDirectory(sessionRoot);

        IPtyConnection? pty = null;
        try
        {
            var options = new PtyOptions
            {
                Name = $"ild-{provider.Type}",
                Cols = Math.Clamp(initialCols, 20, 500),
                Rows = Math.Clamp(initialRows, 5, 200),
                Cwd = sessionRoot,
                App = binaryPath,
                CommandLine = Array.Empty<string>(),
                Environment = new Dictionary<string, string>(),
            };

            try
            {
                pty = await PtyProvider.SpawnAsync(options, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to spawn PTY for provider {ProviderId} ({Binary})", provider.Id, binaryPath);
                await SendErrorAndCloseAsync(
                    socket,
                    $"Failed to start '{binaryPath}': {ex.Message}",
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
            // token, so we must kill the child process to unblock ReadAsync
            // before awaiting the pump tasks to drain.
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
            try { Directory.Delete(sessionRoot, recursive: true); } catch { }
        }
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

    private static string ResolveBinaryPath(AiProvider provider)
    {
        var fallback = string.IsNullOrWhiteSpace(provider.Type) ? "" : provider.Type.ToLowerInvariant();
        if (string.IsNullOrEmpty(provider.Config)) return fallback;

        try
        {
            using var doc = JsonDocument.Parse(provider.Config);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("binaryPath", out var bp)
                && bp.ValueKind == JsonValueKind.String)
            {
                var value = bp.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
        }
        catch { }

        return fallback;
    }

    private static async Task SendErrorAndCloseAsync(WebSocket socket, string message, CancellationToken ct)
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
