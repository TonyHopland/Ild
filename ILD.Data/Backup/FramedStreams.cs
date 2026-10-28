using System.Buffers.Binary;

#if WORKITEM_SERVER
namespace ILD.WorkItemServer.Backup;
#else
namespace ILD.Data.Backup;
#endif

/// <summary>
/// The rows of a table as a run of length-prefixed frames ended by an empty one, so a reader can
/// tell where a table stops without parsing its rows, and a stream cut short is an error and not
/// a shorter table.
/// </summary>
internal static class Framing
{
    public const int MaxFrame = 1024 * 1024;

    /// <summary>Written after the last table. Not a valid frame length, so it cannot be mistaken for data.</summary>
    public const int EndMarker = -1;

    public static async Task WriteEndMarkerAsync(Stream output, CancellationToken ct)
    {
        var marker = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(marker, EndMarker);
        await output.WriteAsync(marker, ct);
    }

    public static async Task RequireEndMarkerAsync(Stream input, CancellationToken ct)
    {
        var marker = new byte[4];
        try { await input.ReadExactlyAsync(marker, ct); }
        catch (EndOfStreamException) { throw Incomplete(); }
        if (BinaryPrimitives.ReadInt32BigEndian(marker) != EndMarker) throw Incomplete();
    }

    public static BackupRefusedException Incomplete()
        => new("The backup is incomplete or damaged: it does not end where it should.");
}

internal sealed class FramedWriteStream(Stream inner) : ForwardOnlyStream(canRead: false, canWrite: true)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _filled;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        while (data.Length > 0)
        {
            var take = Math.Min(data.Length, _buffer.Length - _filled);
            data[..take].CopyTo(_buffer.AsMemory(_filled));
            _filled += take;
            data = data[take..];
            if (_filled == _buffer.Length) await EmitAsync(ct);
        }
    }

    /// <summary>Writes what is buffered and the empty frame that ends the table.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        await EmitAsync(ct);
        await EmitFrameAsync(ReadOnlyMemory<byte>.Empty, ct);
    }

    private async Task EmitAsync(CancellationToken ct)
    {
        if (_filled == 0) return;
        await EmitFrameAsync(_buffer.AsMemory(0, _filled), ct);
        _filled = 0;
    }

    private async Task EmitFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, frame.Length);
        await inner.WriteAsync(length, ct);
        await inner.WriteAsync(frame, ct);
    }

}

internal sealed class FramedReadStream(Stream inner) : ForwardOnlyStream(canRead: true, canWrite: false)
{
    private int _remaining;
    private bool _ended;

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
    {
        if (_ended || destination.Length == 0) return 0;
        if (_remaining == 0)
        {
            var header = new byte[4];
            try { await inner.ReadExactlyAsync(header, ct); }
            catch (EndOfStreamException) { throw Framing.Incomplete(); }
            _remaining = BinaryPrimitives.ReadInt32BigEndian(header);
            if (_remaining is < 0 or > Framing.MaxFrame) throw Framing.Incomplete();
            if (_remaining == 0)
            {
                _ended = true;
                return 0;
            }
        }

        var read = await inner.ReadAsync(destination[..Math.Min(destination.Length, _remaining)], ct);
        if (read == 0) throw Framing.Incomplete();
        _remaining -= read;
        return read;
    }

}
