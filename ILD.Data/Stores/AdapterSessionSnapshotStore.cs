using System.Data;
using System.Linq.Expressions;
using System.Text;
using ILD.Data.Entities;
using ILD.Data.Migrations;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        // Legacy (remove with migration B): whether the row still holds its content in the old column.
        EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn) != null);

    /// <summary>The raw line bytes a page holds at most; a single longer line is cut off at it.</summary>
    private const int PageByteBudget = 1024 * 1024;

    private readonly AppDbContext _db;

    public AdapterSessionSnapshotStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SessionSnapshotMetadata>> ListMetadataForRunAsync(Guid loopRunId, CancellationToken cancellationToken = default)
        => await _db.AdapterSessionSnapshots
            .Where(s => s.LoopRunId == loopRunId)
            .OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt)
            .ThenBy(s => s.AdapterName)
            .ThenBy(s => s.SessionId)
            .Select(ToMetadata)
            .ToListAsync(cancellationToken);

    public async Task<SessionSnapshotMetadata?> GetMetadataAsync(SessionSnapshotKey key, CancellationToken cancellationToken = default)
    {
        var metadata = await Snapshots(key).Select(ToMetadata).FirstOrDefaultAsync(cancellationToken);
        if (metadata is not { Unconverted: true }) return metadata;

        // Legacy (remove with migration B): a row still in the old column is converted before its metadata is used.
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
            // and the loser of a race sees the winner's length and matches nothing.
            var claimed = await _db.AdapterSessionSnapshots
                .Where(s => s.Id == id
                    && s.ByteLength == expectedByteLength
                    // Legacy (remove with migration B): a row still in the old column is not appended to.
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
        await ReplaceWithinAsync(key, format, fullRaw, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>The body of <see cref="ReplaceAsync"/>, inside a transaction the caller owns.</summary>
    private async Task ReplaceWithinAsync(SessionSnapshotKey key, string format, Stream fullRaw, CancellationToken cancellationToken)
    {
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
                // Legacy (remove with migration B): replacing a row still in the old column empties it.
                .SetProperty(s => EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn), (string?)null)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(SessionSnapshotKey key, long fromRawOffset = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromRawOffset);
        var tx = await BeginConsistentReadAsync(key, cancellationToken);
        if (tx is null) return null;
        try
        {
            return await OpenContentAsync(tx.Value.Metadata.Id, fromRawOffset, tx.Value.Transaction, cancellationToken);
        }
        catch
        {
            await tx.Value.Transaction.DisposeAsync();
            throw;
        }
    }

    public async Task<SessionSnapshotPage?> ReadHeadAsync(SessionSnapshotKey key, CancellationToken cancellationToken = default)
    {
        if (await BeginConsistentReadAsync(key, cancellationToken) is not ({ } tx, { } metadata))
            return null;
        await using var _ = tx;
        if (metadata.ByteLength == 0)
            return Page(metadata, [], 0, truncated: false);

        var bytes = new byte[Math.Min(metadata.ByteLength, PageByteBudget)];
        await using (var content = await OpenContentAsync(metadata.Id, 0, ownedTransaction: null, cancellationToken))
            await content.ReadExactlyAsync(bytes, cancellationToken);
        var truncated = metadata.ByteLength > bytes.Length;
        return Page(metadata, [Encoding.UTF8.GetString(bytes, 0, truncated ? WholeUtf8Length(bytes) : bytes.Length)], 0, truncated);
    }

    public async Task<SessionSnapshotPage?> ReadTailAsync(
        SessionSnapshotKey key, int maxEvents, long? beforeRawOffset, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);
        if (beforeRawOffset is { } requested) ArgumentOutOfRangeException.ThrowIfNegative(requested);
        if (await BeginConsistentReadAsync(key, cancellationToken) is not ({ } tx, { } metadata))
            return null;
        await using var _ = tx;
        var before = Math.Min(beforeRawOffset ?? metadata.ByteLength, metadata.ByteLength);

        // Walk back a chunk at a time, keeping each chunk once. A line is only returned once
        // its start has been read: the newline before it, or the start of the content.
        var chunks = await ChunkHeadersAsync(metadata.Id, 0, cancellationToken);
        var read = new List<(long Offset, byte[] Data, int Length)>();
        var newlines = new List<long>();
        var startsAfterNewlines = 0;
        var regionStart = before;
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            var chunk = chunks[i];
            if (chunk.RawOffset >= before) continue;
            var data = SessionSnapshotChunker.Decompress(await ChunkDataAsync(metadata.Id, chunk.Seq, cancellationToken), chunk.RawLength);
            var length = (int)Math.Min(chunk.RawLength, before - chunk.RawOffset);
            read.Add((chunk.RawOffset, data, length));
            for (var at = data.AsSpan(0, length).LastIndexOf((byte)'\n'); at >= 0; at = data.AsSpan(0, at).LastIndexOf((byte)'\n'))
            {
                newlines.Add(chunk.RawOffset + at);
                if (chunk.RawOffset + at + 1 < before) startsAfterNewlines++;
            }
            regionStart = chunk.RawOffset;

            var starts = startsAfterNewlines + (regionStart == 0 ? 1 : 0);
            if (starts >= maxEvents || (starts > 0 && before - regionStart >= PageByteBudget)) break;
        }

        newlines.Reverse();
        var lineStarts = new List<long>();
        if (regionStart == 0 && before > 0) lineStarts.Add(0);
        lineStarts.AddRange(newlines.Where(n => n + 1 < before).Select(n => n + 1));
        var lastLineEnd = newlines.Count > 0 && newlines[^1] == before - 1 ? before - 1 : before;
        long End(int n) => n + 1 < lineStarts.Count ? lineStarts[n + 1] - 1 : lastLineEnd;

        var first = lineStarts.Count;
        var pageBytes = 0L;
        while (first > 0 && lineStarts.Count - first < maxEvents)
        {
            var length = End(first - 1) - lineStarts[first - 1];
            if (first < lineStarts.Count && pageBytes + length > PageByteBudget) break;
            pageBytes += length;
            first--;
        }

        var events = new List<string>(lineStarts.Count - first);
        var truncated = false;
        for (var n = first; n < lineStarts.Count; n++)
        {
            var length = End(n) - lineStarts[n];
            var bytes = Copy(read, lineStarts[n], (int)Math.Min(length, PageByteBudget));
            var kept = bytes.Length;
            if (length > PageByteBudget)
            {
                truncated = true;
                kept = WholeUtf8Length(bytes);
            }
            events.Add(Encoding.UTF8.GetString(bytes, 0, kept));
        }
        return Page(metadata, events, first < lineStarts.Count ? lineStarts[first] : before, truncated);
    }

    /// <summary>The raw bytes <c>[offset, offset + length)</c> out of chunks read in reverse order.</summary>
    private static byte[] Copy(List<(long Offset, byte[] Data, int Length)> read, long offset, int length)
    {
        var bytes = new byte[length];
        foreach (var (chunkOffset, data, chunkLength) in read)
        {
            var from = Math.Max(offset, chunkOffset);
            var to = Math.Min(offset + length, chunkOffset + chunkLength);
            if (from < to)
                data.AsSpan((int)(from - chunkOffset), (int)(to - from)).CopyTo(bytes.AsSpan((int)(from - offset)));
        }
        return bytes;
    }

    /// <summary>How much of <paramref name="bytes"/> holds whole UTF-8 characters, leaving out one cut off at the end.</summary>
    private static int WholeUtf8Length(ReadOnlySpan<byte> bytes)
    {
        var lead = bytes.Length - 1;
        while (lead >= 0 && lead >= bytes.Length - 4 && (bytes[lead] & 0xC0) == 0x80) lead--;
        if (lead < 0) return bytes.Length;
        var width = bytes[lead] switch { < 0x80 => 1, >= 0xF0 => 4, >= 0xE0 => 3, >= 0xC0 => 2, _ => 1 };
        return lead + width > bytes.Length ? lead : bytes.Length;
    }

    /// <summary>A page whose previous page ends where it starts, unless it starts the content.</summary>
    private static SessionSnapshotPage Page(SessionSnapshotMetadata snapshot, IReadOnlyList<string> events, long fromOffset, bool truncated)
        => new(snapshot, events, fromOffset, fromOffset > 0 ? fromOffset : null, truncated);

    public async Task<bool> ForkAsync(SessionSnapshotKey source, SessionSnapshotKey destination, CancellationToken cancellationToken = default)
    {
        if (source == destination) return await Snapshots(source).AnyAsync(cancellationToken);

        // One repeatable-read transaction reads the source and writes the copy, so the copy is
        // of one version of the source. A concurrent write to the destination makes Postgres
        // fail ours once that write commits, with a serialization failure or, when both created
        // the destination row, a unique violation; the copy is then made again, once.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (await BeginConsistentReadAsync(source, cancellationToken) is not ({ } tx, { } metadata)) return false;
                await using var _ = tx;
                await using var content = await OpenContentAsync(metadata.Id, 0, ownedTransaction: null, cancellationToken);
                await using var rewritten = new LineWiseReplacingStream(
                    content, Encoding.UTF8.GetBytes(source.SessionId), Encoding.UTF8.GetBytes(destination.SessionId));
                await ReplaceWithinAsync(destination, metadata.Format, rewritten, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return true;
            }
            catch (Exception ex) when (attempt == 1 && (DbErrors.IsSerializationFailure(ex) || ex is DbUpdateException update && DbErrors.IsUniqueViolation(update)))
            {
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// A repeatable-read transaction and the snapshot's metadata as of it; null when there is no snapshot. Every chunk read inside it sees the
    /// content the metadata describes, even while a replace swaps that content out.
    /// </summary>
    private async Task<(IDbContextTransaction Transaction, SessionSnapshotMetadata Metadata)?> BeginConsistentReadAsync(
        SessionSnapshotKey key, CancellationToken ct)
    {
        // Legacy (remove with migration B): GetMetadataAsync converts a row still in the old column first.
        if (await GetMetadataAsync(key, ct) is null) return null;

        var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        try
        {
            var metadata = await Snapshots(key).Select(ToMetadata).FirstOrDefaultAsync(ct);
            if (metadata is not null) return (tx, metadata);
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
        await tx.DisposeAsync();
        return null;
    }

    private async Task<Stream> OpenContentAsync(Guid snapshotId, long fromRawOffset, IDbContextTransaction? ownedTransaction, CancellationToken ct)
        => new SessionSnapshotContentStream(_db, snapshotId, await ChunkHeadersAsync(snapshotId, fromRawOffset, ct), fromRawOffset, ownedTransaction);

    private Task<byte[]> ChunkDataAsync(Guid snapshotId, int seq, CancellationToken ct)
        => _db.AdapterSessionSnapshotChunks
            .Where(c => c.SnapshotId == snapshotId && c.Seq == seq)
            .Select(c => c.Data)
            .SingleAsync(ct);

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
