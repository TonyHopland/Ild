using System.Linq.Expressions;
using System.Text;
using ILD.Data.Entities;
using ILD.Data.Migrations;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class AdapterSessionSnapshotStore : IAdapterSessionSnapshotStore
{
    private static readonly Expression<Func<AdapterSessionSnapshot, SessionSnapshotMetadata>> ToMetadata = s => new SessionSnapshotMetadata(
        s.Id,
        s.LoopRunId,
        s.ChatSessionId,
        s.AdapterName,
        s.SessionId,
        s.Format,
        s.Compression,
        s.ByteLength,
        s.ChunkCount,
        s.EventCount,
        s.TailHash,
        s.CreatedAt,
        s.UpdatedAt,
        EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn) != null);

    private readonly AppDbContext _db;

    public AdapterSessionSnapshotStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<SessionSnapshotMetadata>> ListMetadataForRunAsync(Guid loopRunId, CancellationToken cancellationToken = default)
        => ListAsync(_db.AdapterSessionSnapshots.Where(s => s.LoopRunId == loopRunId), cancellationToken);

    public Task<IReadOnlyList<SessionSnapshotMetadata>> ListMetadataForChatAsync(Guid chatSessionId, CancellationToken cancellationToken = default)
        => ListAsync(_db.AdapterSessionSnapshots.Where(s => s.ChatSessionId == chatSessionId), cancellationToken);

    private static async Task<IReadOnlyList<SessionSnapshotMetadata>> ListAsync(IQueryable<AdapterSessionSnapshot> snapshots, CancellationToken ct)
        => await snapshots
            .OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt)
            .ThenBy(s => s.AdapterName)
            .ThenBy(s => s.SessionId)
            .Select(ToMetadata)
            .ToListAsync(ct);

    public async Task<SessionSnapshotMetadata?> GetMetadataAsync(SessionSnapshotKey key, CancellationToken cancellationToken = default)
    {
        var metadata = await Snapshots(key).Select(ToMetadata).FirstOrDefaultAsync(cancellationToken);
        if (metadata is not { Unconverted: true }) return metadata;

        await SessionSnapshotContentMigrator.ConvertRowAsync(_db, metadata.Id, cancellationToken);
        return await Snapshots(key).Select(ToMetadata).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<SessionSnapshotAppendResult> AppendAsync(
        SessionSnapshotKey key, string format, Stream newRaw, long expectedByteLength, string newTailHash,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var id = await Snapshots(key).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        int firstSeq;
        if (id is null)
        {
            if (expectedByteLength != 0) return SessionSnapshotAppendResult.Conflict;
            id = Guid.NewGuid();
            try
            {
                await InsertAsync(NewRow(id.Value, key, format), cancellationToken);
            }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
            {
                return SessionSnapshotAppendResult.Conflict;
            }
            firstSeq = 0;
        }
        else
        {
            // Claiming the row takes its lock, so appends to one snapshot run one at a time
            // and the loser of a race sees the winner's length and matches nothing. A row
            // still holding legacy content never gets chunks beside it.
            var claimed = await _db.AdapterSessionSnapshots
                .Where(s => s.Id == id
                    && s.ByteLength == expectedByteLength
                    && EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn) == null)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (claimed == 0) return SessionSnapshotAppendResult.Conflict;
            firstSeq = await _db.AdapterSessionSnapshots.Where(s => s.Id == id).Select(s => s.ChunkCount).SingleAsync(cancellationToken);
        }

        var chunker = new SessionSnapshotChunker(id.Value, firstSeq, expectedByteLength, InsertAsync);
        try
        {
            await chunker.CopyFromAsync(newRaw, cancellationToken);
            await chunker.CompleteAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return SessionSnapshotAppendResult.Conflict;
        }

        await _db.AdapterSessionSnapshots
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Format, format)
                .SetProperty(s => s.Compression, SessionSnapshotFormats.Brotli)
                .SetProperty(s => s.ByteLength, s => s.ByteLength + chunker.BytesWritten)
                .SetProperty(s => s.ChunkCount, s => s.ChunkCount + chunker.ChunksWritten)
                .SetProperty(s => s.EventCount, s => s.EventCount + chunker.LinesWritten)
                .SetProperty(s => s.TailHash, newTailHash)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return SessionSnapshotAppendResult.Appended;
    }

    public async Task ReplaceAsync(SessionSnapshotKey key, string format, Stream fullRaw, CancellationToken cancellationToken = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var id = await Snapshots(key).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (id is null)
        {
            id = Guid.NewGuid();
            await InsertAsync(NewRow(id.Value, key, format), cancellationToken);
        }
        else
        {
            // Lock the row before its chunks go, so a concurrent append waits and then
            // finds the length changed.
            await _db.AdapterSessionSnapshots
                .Where(s => s.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.UpdatedAt, DateTime.UtcNow), cancellationToken);
            await _db.AdapterSessionSnapshotChunks.Where(c => c.SnapshotId == id).ExecuteDeleteAsync(cancellationToken);
        }

        var chunker = new SessionSnapshotChunker(id.Value, 0, 0, InsertAsync);
        await chunker.CopyFromAsync(fullRaw, cancellationToken);
        await chunker.CompleteAsync(cancellationToken);

        await _db.AdapterSessionSnapshots
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Format, format)
                .SetProperty(s => s.Compression, SessionSnapshotFormats.Brotli)
                .SetProperty(s => s.ByteLength, chunker.BytesWritten)
                .SetProperty(s => s.ChunkCount, chunker.ChunksWritten)
                .SetProperty(s => s.EventCount, chunker.LinesWritten)
                .SetProperty(s => s.TailHash, chunker.TailHash)
                .SetProperty(s => EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn), (string?)null)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(SessionSnapshotKey key, long fromRawOffset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromRawOffset);
        var metadata = await GetMetadataAsync(key, cancellationToken);
        return metadata is null ? null : await OpenContentAsync(metadata.Id, fromRawOffset, cancellationToken);
    }

    public async Task<SessionSnapshotPage> ReadRangeAsync(SessionSnapshotKey key, long fromRawOffset, int maxEvents, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromRawOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);
        var metadata = await GetMetadataAsync(key, cancellationToken);
        if (metadata is null || fromRawOffset >= metadata.ByteLength)
            return new SessionSnapshotPage([], fromRawOffset, null);

        await using var content = await OpenContentAsync(metadata.Id, fromRawOffset, cancellationToken);
        var events = new List<string>();
        var line = new MemoryStream();
        var consumed = 0L;
        var block = new byte[81920];
        while (events.Count < maxEvents)
        {
            var read = await content.ReadAsync(block, cancellationToken);
            if (read == 0)
            {
                if (line.Length > 0) events.Add(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                break;
            }

            var span = block.AsSpan(0, read);
            while (events.Count < maxEvents)
            {
                var newline = span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    line.Write(span);
                    consumed += span.Length;
                    break;
                }
                line.Write(span[..newline]);
                events.Add(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length));
                line.SetLength(0);
                consumed += newline + 1;
                span = span[(newline + 1)..];
            }
        }

        var next = fromRawOffset + consumed;
        return new SessionSnapshotPage(events, fromRawOffset, next < metadata.ByteLength ? next : null);
    }

    public async Task<SessionSnapshotPage> ReadTailAsync(SessionSnapshotKey key, int maxEvents, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);
        var metadata = await GetMetadataAsync(key, cancellationToken);
        if (metadata is null) return new SessionSnapshotPage([], 0, null);

        var chunks = await ChunkHeadersAsync(metadata.Id, 0, cancellationToken);
        var tail = Array.Empty<byte>();
        var tailOffset = metadata.ByteLength;
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            var chunk = chunks[i];
            var data = await _db.AdapterSessionSnapshotChunks
                .Where(c => c.SnapshotId == metadata.Id && c.Seq == chunk.Seq)
                .Select(c => c.Data)
                .SingleAsync(cancellationToken);
            tail = [.. SessionSnapshotChunker.Decompress(data, chunk.RawLength), .. tail];
            tailOffset = chunk.RawOffset;

            // A chunk can start mid-line when the write before it did not end on one, so
            // the first line of what has been read is only whole once the start is reached.
            var whole = LineStarts(tail).Count - (i > 0 ? 1 : 0);
            if (whole >= maxEvents) break;
        }

        var starts = LineStarts(tail);
        var first = Math.Max(0, starts.Count - maxEvents);
        var events = new List<string>(starts.Count - first);
        for (var n = first; n < starts.Count; n++)
        {
            var end = n + 1 < starts.Count ? starts[n + 1] - 1 : (tail.Length > 0 && tail[^1] == (byte)'\n' ? tail.Length - 1 : tail.Length);
            events.Add(Encoding.UTF8.GetString(tail, starts[n], end - starts[n]));
        }
        return new SessionSnapshotPage(events, tailOffset + (first < starts.Count ? starts[first] : tail.Length), null);
    }

    /// <summary>Where each line in <paramref name="content"/> starts; a trailing newline starts no line.</summary>
    private static List<int> LineStarts(byte[] content)
    {
        var starts = new List<int>();
        var position = 0;
        while (position < content.Length)
        {
            starts.Add(position);
            var newline = content.AsSpan(position).IndexOf((byte)'\n');
            if (newline < 0) break;
            position += newline + 1;
        }
        return starts;
    }

    public async Task<bool> ForkAsync(SessionSnapshotKey source, SessionSnapshotKey destination, CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(source, cancellationToken);
        if (metadata is null) return false;
        if (source == destination) return true;

        await using var content = await OpenContentAsync(metadata.Id, 0, cancellationToken);
        await using var rewritten = new LineWiseReplacingStream(
            content, Encoding.UTF8.GetBytes(source.SessionId), Encoding.UTF8.GetBytes(destination.SessionId));
        await ReplaceAsync(destination, metadata.Format, rewritten, cancellationToken);
        return true;
    }

    private async Task<Stream> OpenContentAsync(Guid snapshotId, long fromRawOffset, CancellationToken ct)
        => new SessionSnapshotContentStream(_db, snapshotId, await ChunkHeadersAsync(snapshotId, fromRawOffset, ct), fromRawOffset);

    private async Task<IReadOnlyList<SessionSnapshotChunkHeader>> ChunkHeadersAsync(Guid snapshotId, long fromRawOffset, CancellationToken ct)
        => await _db.AdapterSessionSnapshotChunks
            .Where(c => c.SnapshotId == snapshotId && c.RawOffset + c.RawLength > fromRawOffset)
            .OrderBy(c => c.Seq)
            .Select(c => new SessionSnapshotChunkHeader(c.Seq, c.RawOffset, c.RawLength))
            .ToListAsync(ct);

    /// <summary>Inserts one row on its own, leaving nothing tracked whether or not the insert lands.</summary>
    private async Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct) where TEntity : class
    {
        _db.Add(entity);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            _db.Entry(entity).State = EntityState.Detached;
        }
    }

    private IQueryable<AdapterSessionSnapshot> Snapshots(SessionSnapshotKey key)
    {
        var (adapterName, sessionId) = (key.AdapterName, key.SessionId);
        if (key.ChatSessionId is { } chatSessionId)
            return _db.AdapterSessionSnapshots.Where(s => s.ChatSessionId == chatSessionId && s.AdapterName == adapterName && s.SessionId == sessionId);
        if (key.LoopRunId is { } loopRunId)
            return _db.AdapterSessionSnapshots.Where(s => s.LoopRunId == loopRunId && s.AdapterName == adapterName && s.SessionId == sessionId);
        throw new ArgumentException("A session snapshot key names a run or a chat.", nameof(key));
    }

    private static AdapterSessionSnapshot NewRow(Guid id, SessionSnapshotKey key, string format) => new()
    {
        Id = id,
        LoopRunId = key.ChatSessionId is null ? key.LoopRunId : null,
        ChatSessionId = key.ChatSessionId,
        AdapterName = key.AdapterName,
        SessionId = key.SessionId,
        Format = format,
        Compression = SessionSnapshotFormats.Brotli,
    };
}
