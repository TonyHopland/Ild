using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ILD.Data.Stores;

/// <summary>Where a stored chunk sits in the raw content, without its data.</summary>
internal sealed record SessionSnapshotChunkHeader(int Seq, long RawOffset, int RawLength);

/// <summary>
/// A snapshot's raw content from a given offset, fetching and decompressing one chunk
/// at a time and only when the reader gets to it. Lives as long as the context it reads through,
/// and ends the read transaction it is given when it is disposed.
/// </summary>
internal sealed class SessionSnapshotContentStream : Stream
{
    private readonly AppDbContext _db;
    private readonly Guid _snapshotId;
    private readonly IReadOnlyList<SessionSnapshotChunkHeader> _chunks;
    private readonly long _fromOffset;
    private readonly IDbContextTransaction? _ownedTransaction;
    private int _next;
    private byte[] _current = [];
    private int _position;

    public SessionSnapshotContentStream(AppDbContext db, Guid snapshotId, IReadOnlyList<SessionSnapshotChunkHeader> chunks, long fromOffset,
        IDbContextTransaction? ownedTransaction)
    {
        _db = db;
        _snapshotId = snapshotId;
        _chunks = chunks;
        _fromOffset = fromOffset;
        _ownedTransaction = ownedTransaction;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_position == _current.Length)
        {
            if (_next == _chunks.Count) return 0;
            var chunk = _chunks[_next];
            Load(chunk, ChunkData(chunk.Seq).Single());
        }
        return Take(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_position == _current.Length)
        {
            if (_next == _chunks.Count) return 0;
            var chunk = _chunks[_next];
            Load(chunk, await ChunkData(chunk.Seq).SingleAsync(cancellationToken));
        }
        return Take(buffer.Span);
    }

    private IQueryable<byte[]> ChunkData(int seq)
        => _db.AdapterSessionSnapshotChunks
            .Where(c => c.SnapshotId == _snapshotId && c.Seq == seq)
            .Select(c => c.Data);

    private void Load(SessionSnapshotChunkHeader chunk, byte[] data)
    {
        _current = SessionSnapshotChunker.Decompress(data, chunk.RawLength);
        _position = _next == 0 ? (int)Math.Max(0, _fromOffset - chunk.RawOffset) : 0;
        _next++;
    }

    private int Take(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _current.Length - _position);
        _current.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ownedTransaction?.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_ownedTransaction is not null) await _ownedTransaction.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Raw content with every occurrence of one byte sequence replaced by another. It works
/// a run of whole lines at a time, so an occurrence that holds no newline, such as a
/// session id, is never split between two runs and so never missed.
/// </summary>
internal sealed class LineWiseReplacingStream : Stream
{
    private const int BlockSize = 81920;

    private readonly Stream _source;
    private readonly byte[] _find;
    private readonly byte[] _replacement;
    private byte[] _pending = new byte[BlockSize];
    private int _pendingLength;
    private bool _sourceEnded;
    private byte[] _output = [];
    private int _outputPosition;

    public LineWiseReplacingStream(Stream source, byte[] find, byte[] replacement)
    {
        if (find.Length == 0 || find.Contains((byte)'\n'))
            throw new ArgumentException("The replaced sequence must be non-empty and hold no newline.", nameof(find));
        _source = source;
        _find = find;
        _replacement = replacement;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Only read asynchronously: a fork copies it into the store with async I/O, and nothing reads it otherwise.</summary>
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("Read this stream asynchronously.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_outputPosition == _output.Length)
        {
            if (_sourceEnded && _pendingLength == 0) return 0;
            await FillOutputAsync(cancellationToken);
        }

        var count = Math.Min(buffer.Length, _output.Length - _outputPosition);
        _output.AsMemory(_outputPosition, count).CopyTo(buffer);
        _outputPosition += count;
        return count;
    }

    private async Task FillOutputAsync(CancellationToken ct)
    {
        while (true)
        {
            var lastNewline = _pending.AsSpan(0, _pendingLength).LastIndexOf((byte)'\n');
            if (lastNewline >= 0 || _sourceEnded)
            {
                var length = _sourceEnded ? _pendingLength : lastNewline + 1;
                _output = Replace(_pending.AsSpan(0, length));
                _outputPosition = 0;
                _pending.AsSpan(length, _pendingLength - length).CopyTo(_pending);
                _pendingLength -= length;
                return;
            }

            if (_pending.Length - _pendingLength < BlockSize)
                Array.Resize(ref _pending, _pending.Length * 2);
            var read = await _source.ReadAsync(_pending.AsMemory(_pendingLength, BlockSize), ct);
            if (read == 0) _sourceEnded = true;
            _pendingLength += read;
        }
    }

    private byte[] Replace(ReadOnlySpan<byte> run)
    {
        using var result = new MemoryStream(run.Length);
        while (true)
        {
            var at = run.IndexOf(_find);
            if (at < 0) break;
            result.Write(run[..at]);
            result.Write(_replacement);
            run = run[(at + _find.Length)..];
        }
        result.Write(run);
        return result.ToArray();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
