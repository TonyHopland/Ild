using System.Buffers.Binary;
using System.Net.Sockets;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Core.Services.RunHosts.Container;

/// <summary>
/// One command running in a container, over the hijacked connection of the Docker exec API. Without a terminal the
/// stream carries stdout and stderr as frames of an eight byte header and the bytes; with one it is the raw terminal.
/// Stdin ends when this side half-closes the connection.
/// </summary>
internal sealed class DockerExec : IExecStream
{
    private readonly DockerApiClient _api;
    private readonly string _execId;
    private readonly bool _tty;
    private readonly TimeSpan _statusTimeout;
    private readonly Socket _socket;
    private readonly NetworkStream _network;
    private readonly Stream _reader;
    private readonly ByteChannelStream _stdout = new();
    private readonly ByteChannelStream? _stderr;
    private readonly CancellationTokenSource _disposed = new();
    private readonly CancellationToken _gone;
    private int _closed;

    private DockerExec(DockerApiClient api, string execId, bool tty, TimeSpan statusTimeout, (Socket Socket, NetworkStream Network, Stream Reader) connection)
    {
        _api = api;
        _execId = execId;
        _tty = tty;
        _statusTimeout = statusTimeout;
        (_socket, _network, _reader) = connection;
        _gone = _disposed.Token;
        _stderr = tty ? null : new ByteChannelStream();
        Status = Task.Run(PumpAsync);
    }

    public Stream Stdout => _stdout;

    public Stream? Stderr => _stderr;

    public Task<int?> Status { get; }

    /// <summary>How long a stream that has ended may take to report its exit code before the command counts as dropped.</summary>
    public static readonly TimeSpan DefaultStatusTimeout = TimeSpan.FromSeconds(5);

    public static async Task<DockerExec> OpenAsync(
        DockerApiClient api, string container, IReadOnlyList<string> command, bool stdin, bool tty, TimeSpan statusTimeout, CancellationToken ct, string? user = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var id = await api.CreateExecAsync(container, command, stdin, tty, ct, user, environment);
        return new DockerExec(api, id, tty, statusTimeout, await api.StartExecAsync(id, tty, ct));
    }

    public async Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _network.WriteAsync(data, ct);
        await _network.FlushAsync(ct);
    }

    public Task CloseStdinAsync(CancellationToken ct = default)
    {
        try { _socket.Shutdown(SocketShutdown.Send); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
        => _api.ResizeExecAsync(_execId, cols, rows, ct);

    private async Task<int?> PumpAsync()
    {
        try
        {
            if (_tty) await CopyRawAsync(); else await DemultiplexAsync();
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The connection broke: whether the command finished is told by the engine, or by its silence.
        }
        finally
        {
            _stdout.Complete();
            _stderr?.Complete();
        }
        return await ExitCodeAsync();
    }

    private async Task CopyRawAsync()
    {
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await _reader.ReadAsync(buffer, _gone)) > 0)
            _stdout.Append(buffer.AsSpan(0, read));
    }

    private async Task DemultiplexAsync()
    {
        var header = new byte[8];
        var buffer = new byte[16 * 1024];
        while (true)
        {
            try { await _reader.ReadExactlyAsync(header, _gone); }
            catch (EndOfStreamException) { return; }

            var remaining = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            // Type 3 carries the daemon's own error text; it goes where the command's errors go.
            var target = header[0] is 2 or 3 && _stderr is not null ? _stderr : _stdout;
            while (remaining > 0)
            {
                var read = await _reader.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, (uint)buffer.Length)), _gone);
                if (read == 0) return;
                if (header[0] is 1 or 2 or 3) target.Append(buffer.AsSpan(0, read));
                remaining -= (uint)read;
            }
        }
    }

    // The engine marks the exec finished a moment after its stream ends. A command still running when the stream is gone was cut off.
    private async Task<int?> ExitCodeAsync()
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_gone);
        limit.CancelAfter(_statusTimeout);
        try
        {
            while (true)
            {
                if (await _api.ExecExitCodeAsync(_execId, limit.Token) is { } code) return code;
                await Task.Delay(50, limit.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or DockerApiException or IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _disposed.Cancel();
        _disposed.Dispose();
        _reader.Dispose();
        _network.Dispose();
        _socket.Dispose();
    }
}
