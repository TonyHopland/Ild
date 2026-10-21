using System.Buffers;
using System.IO.Compression;
using ILD.Data.Entities;

namespace ILD.Data.Stores;

/// <summary>
/// Cuts raw session content into compressed chunks of about <see cref="Target"/> raw
/// bytes, each ending right after a newline, and hands each to a sink as soon as it is
/// cut. A chunk outgrows the target only to hold a single longer line; the last chunk
/// of a write ends wherever the input does. Holds at most one chunk's raw bytes.
/// </summary>
internal sealed class SessionSnapshotChunker
{
    public const int Target = 4 * 1024 * 1024;

    private const int BrotliQuality = 5;
    private const int BrotliWindow = 22;

    private readonly Guid _snapshotId;
    private readonly Func<AdapterSessionSnapshotChunk, CancellationToken, Task> _sink;
    private readonly byte[] _tail = new byte[SessionSnapshotFormats.TailWindow];
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;
    private int _scannedForLineEnd;
    private int _tailLength;
    private int _nextSeq;
    private long _nextOffset;

    public SessionSnapshotChunker(
        Guid snapshotId, int firstSeq, long firstRawOffset, Func<AdapterSessionSnapshotChunk, CancellationToken, Task> sink)
    {
        _snapshotId = snapshotId;
        _nextSeq = firstSeq;
        _nextOffset = firstRawOffset;
        _sink = sink;
    }

    public long BytesWritten { get; private set; }

    public int ChunksWritten { get; private set; }

    /// <summary>Newlines written.</summary>
    public int LinesWritten { get; private set; }

    /// <summary>The <see cref="SessionSnapshotFormats.TailHash"/> of what this chunker was given.</summary>
    public string TailHash => SessionSnapshotFormats.TailHash(_tail.AsSpan(0, _tailLength));

    public async Task CopyFromAsync(Stream source, CancellationToken ct)
    {
        var block = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await source.ReadAsync(block.AsMemory(0, 81920), ct)) > 0)
                await WriteAsync(block.AsMemory(0, read), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Append(data.Span);
        while (_length >= Target && FindCut() is { } cut)
        {
            await EmitAsync(cut, ct);
        }
    }

    /// <summary>Emits whatever is left as the last chunk of this write.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_length > 0)
            await EmitAsync(_length, ct);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
        BytesWritten += data.Length;
        RememberTail(data);
    }

    private void RememberTail(ReadOnlySpan<byte> data)
    {
        if (data.Length >= _tail.Length)
        {
            data[^_tail.Length..].CopyTo(_tail);
            _tailLength = _tail.Length;
            return;
        }

        var keep = Math.Min(_tailLength, _tail.Length - data.Length);
        _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
        data.CopyTo(_tail.AsSpan(keep));
        _tailLength = keep + data.Length;
    }

    /// <summary>
    /// The length of the next chunk: up to the last newline within the target, else
    /// through the first newline after it (one line longer than the target), else null
    /// while that line has not ended yet.
    /// </summary>
    private int? FindCut()
    {
        var lastWithinTarget = _buffer.AsSpan(0, Target).LastIndexOf((byte)'\n');
        if (lastWithinTarget >= 0)
            return lastWithinTarget + 1;

        var from = Math.Max(Target, _scannedForLineEnd);
        var next = _buffer.AsSpan(from, _length - from).IndexOf((byte)'\n');
        if (next < 0)
        {
            _scannedForLineEnd = _length;
            return null;
        }
        return from + next + 1;
    }

    private async Task EmitAsync(int length, CancellationToken ct)
    {
        var raw = _buffer.AsMemory(0, length);
        var chunk = new AdapterSessionSnapshotChunk
        {
            SnapshotId = _snapshotId,
            Seq = _nextSeq,
            RawOffset = _nextOffset,
            RawLength = length,
            Data = Compress(raw.Span),
        };
        var lines = raw.Span.Count((byte)'\n');

        await _sink(chunk, ct);

        _nextSeq++;
        _nextOffset += length;
        ChunksWritten++;
        LinesWritten += lines;
        _buffer.AsSpan(length, _length - length).CopyTo(_buffer);
        _length -= length;
        _scannedForLineEnd = 0;
    }

    private static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        var destination = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(raw.Length));
        try
        {
            if (!BrotliEncoder.TryCompress(raw, destination, out var written, BrotliQuality, BrotliWindow))
                throw new InvalidOperationException("Brotli could not compress a session snapshot chunk.");
            return destination.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(destination);
        }
    }

    /// <summary>The raw bytes of a chunk written by this class.</summary>
    public static byte[] Decompress(byte[] data, int rawLength)
    {
        var raw = new byte[rawLength];
        if (!BrotliDecoder.TryDecompress(data, raw, out var written) || written != rawLength)
            throw new InvalidDataException($"A session snapshot chunk did not decompress to its {rawLength} raw bytes.");
        return raw;
    }
}
