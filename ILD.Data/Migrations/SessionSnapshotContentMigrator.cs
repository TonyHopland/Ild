using System.Data;
using System.Data.Common;
using System.Text;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ILD.Data.Migrations;

/// <summary>
/// Moves a session snapshot written before the metadata/content split out of the legacy
/// <c>SessionJson</c> column into compressed chunks, one row at a time.
///
/// The legacy text is streamed, never held whole: Claude Code's wrapper
/// (<c>{"format":"claude-jsonl","sessionId":…,"events":[…]}</c>) is unwrapped back into
/// the raw JSONL it was built from, each event's JSON text followed by a newline; pi,
/// codex and opencode content is kept verbatim. Content that cannot be read as what its
/// adapter wrote is kept verbatim as <see cref="SessionSnapshotFormats.Raw"/>, which is
/// never restored and never dropped.
///
/// A row is converted in one transaction that both writes its chunks and empties the
/// legacy column, claimed by <c>SessionJson IS NOT NULL</c>: a crash leaves the row as it
/// was, a second pass finds nothing to do, and a row a turn has meanwhile replaced is left
/// alone. Leave this wired up until every deployment has started once on this version;
/// migration B then drops the column.
/// </summary>
public static class SessionSnapshotContentMigrator
{
    /// <summary>The next row still holding legacy content, leaving out <paramref name="skip"/>; null when none is left.</summary>
    public static async Task<Guid?> FindNextAsync(AppDbContext db, IReadOnlyCollection<Guid> skip, CancellationToken ct = default)
    {
        var skipped = skip.ToArray();
        return await db.AdapterSessionSnapshots
            .Where(s => EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn) != null && !skipped.Contains(s.Id))
            .OrderBy(s => s.Id)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Converts one row. False when it holds no legacy content (any more).</summary>
    public static async Task<bool> ConvertRowAsync(AppDbContext db, Guid snapshotId, CancellationToken ct = default)
    {
        var converted = await ReadAsync(db, snapshotId, unwrap: true, ct);
        if (converted is { Format: null })
            converted = await ReadAsync(db, snapshotId, unwrap: false, ct);
        if (converted is null) return false;

        var (format, chunks, chunker) = (converted.Format!, converted.Chunks, converted.Chunker);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.AdapterSessionSnapshots
            .Where(s => s.Id == snapshotId && EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn) != null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Format, format)
                .SetProperty(s => s.Compression, SessionSnapshotFormats.Brotli)
                .SetProperty(s => s.ByteLength, chunker.BytesWritten)
                .SetProperty(s => s.ChunkCount, chunker.ChunksWritten)
                .SetProperty(s => s.EventCount, chunker.LinesWritten)
                .SetProperty(s => s.TailHash, chunker.TailHash)
                .SetProperty(s => EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn), (string?)null), ct);
        if (claimed == 0) return false;
        // A row never holds chunks beside legacy content; clearing any lets one that somehow
        // does convert cleanly instead of failing every read that converts it.
        await db.AdapterSessionSnapshotChunks.Where(c => c.SnapshotId == snapshotId).ExecuteDeleteAsync(ct);

        foreach (var chunk in chunks)
        {
            db.AdapterSessionSnapshotChunks.Add(chunk);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            finally
            {
                db.Entry(chunk).State = EntityState.Detached;
            }
        }
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// The row's content cut into compressed chunks, held until the reader is closed since
    /// no other command can run on the connection while it is open. Null when the row holds
    /// no legacy content; a null <see cref="Conversion.Format"/> when unwrapping failed.
    /// </summary>
    private static async Task<Conversion?> ReadAsync(AppDbContext db, Guid snapshotId, bool unwrap, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                """SELECT "AdapterName", "SessionJson" FROM "AdapterSessionSnapshots" WHERE "Id" = @id AND "SessionJson" IS NOT NULL""";
            command.Parameters.Add(IdParameter(db, command, snapshotId));

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            if (!await reader.ReadAsync(ct)) return null;

            var chunks = new List<AdapterSessionSnapshotChunk>();
            var chunker = new SessionSnapshotChunker(snapshotId, 0, 0, (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            });
            var adapterName = reader.GetString(0);
            using var text = reader.GetTextReader(1);
            var output = new Utf8Output(chunker);

            string? format;
            if (adapterName == "ClaudeCode" && unwrap)
            {
                format = await new ClaudeWrapperScanner(text, output).TryUnwrapAsync(ct) ? SessionSnapshotFormats.ClaudeJsonl : null;
            }
            else
            {
                await output.CopyAsync(text, ct);
                format = adapterName switch
                {
                    "Pi" => SessionSnapshotFormats.PiJsonl,
                    "Codex" => SessionSnapshotFormats.CodexJsonl,
                    "OpenCode" => SessionSnapshotFormats.OpenCodeJson,
                    _ => SessionSnapshotFormats.Raw,
                };
            }
            if (format is null) return new Conversion(null, chunks, chunker);

            await output.FlushAsync(ct);
            await chunker.CompleteAsync(ct);
            return new Conversion(format, chunks, chunker);
        }
        finally
        {
            if (opened)
                await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>An id parameter typed the way the provider stores the key column.</summary>
    private static DbParameter IdParameter(AppDbContext db, DbCommand command, Guid snapshotId)
        => db.Model.FindEntityType(typeof(AdapterSessionSnapshot))!
            .FindProperty(nameof(AdapterSessionSnapshot.Id))!
            .GetRelationalTypeMapping()
            .CreateParameter(command, "id", snapshotId);

    private sealed record Conversion(string? Format, List<AdapterSessionSnapshotChunk> Chunks, SessionSnapshotChunker Chunker);

    /// <summary>Text encoded as UTF-8 into a chunker, a block at a time.</summary>
    private sealed class Utf8Output(SessionSnapshotChunker chunker)
    {
        private readonly Encoder _encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetEncoder();
        private readonly char[] _chars = new char[16384];
        private readonly byte[] _bytes = new byte[16384 * 3 + 3];
        private int _count;

        public async ValueTask WriteAsync(char c, CancellationToken ct)
        {
            if (_count == _chars.Length) await EncodeAsync(flush: false, ct);
            _chars[_count++] = c;
        }

        public async Task CopyAsync(TextReader text, CancellationToken ct)
        {
            int read;
            while ((read = await text.ReadAsync(_chars.AsMemory(_count), ct)) > 0)
            {
                _count += read;
                if (_count == _chars.Length) await EncodeAsync(flush: false, ct);
            }
        }

        public Task FlushAsync(CancellationToken ct) => EncodeAsync(flush: true, ct);

        private async Task EncodeAsync(bool flush, CancellationToken ct)
        {
            var written = _encoder.GetBytes(_chars, 0, _count, _bytes, 0, flush);
            _count = 0;
            await chunker.WriteAsync(_bytes.AsMemory(0, written), ct);
        }
    }

    /// <summary>
    /// Walks the old Claude wrapper object as it streams in and writes each element of its
    /// root <c>events</c> array, exactly as it appears in the text, followed by a newline.
    /// Fails on anything that is not one well-formed wrapper object: no root object, no
    /// <c>events</c> array, unbalanced brackets or strings, or text after the object.
    /// </summary>
    private sealed class ClaudeWrapperScanner(TextReader text, Utf8Output output)
    {
        private readonly char[] _buffer = new char[16384];
        private int _position;
        private int _length;

        public async Task<bool> TryUnwrapAsync(CancellationToken ct)
        {
            if (await NextNonSpaceAsync(ct) != '{') return false;
            var sawEvents = false;
            if (await PeekNonSpaceAsync(ct) == '}')
            {
                await NextAsync(ct);
            }
            else
            {
                while (true)
                {
                    if (await NextNonSpaceAsync(ct) != '"') return false;
                    var key = await ReadKeyAsync(ct);
                    if (key is null || await NextNonSpaceAsync(ct) != ':') return false;

                    if (key == "events" && !sawEvents && await PeekNonSpaceAsync(ct) == '[')
                    {
                        sawEvents = true;
                        await NextAsync(ct);
                        if (!await CopyEventsAsync(ct)) return false;
                    }
                    else if (!await ValueAsync(emit: false, ct))
                    {
                        return false;
                    }

                    var separator = await NextNonSpaceAsync(ct);
                    if (separator == '}') break;
                    if (separator != ',') return false;
                }
            }
            return sawEvents && await NextNonSpaceAsync(ct) == -1;
        }

        private async Task<bool> CopyEventsAsync(CancellationToken ct)
        {
            if (await PeekNonSpaceAsync(ct) == ']')
            {
                await NextAsync(ct);
                return true;
            }
            while (true)
            {
                await PeekNonSpaceAsync(ct);
                if (!await ValueAsync(emit: true, ct)) return false;
                await output.WriteAsync('\n', ct);

                var separator = await NextNonSpaceAsync(ct);
                if (separator == ']') return true;
                if (separator != ',') return false;
            }
        }

        /// <summary>One JSON value starting at the next character, copied out when <paramref name="emit"/>.</summary>
        private async Task<bool> ValueAsync(bool emit, CancellationToken ct)
        {
            var first = await PeekAsync(ct);
            if (first is '"' or '{' or '[')
            {
                var closers = new Stack<char>();
                var inString = false;
                do
                {
                    var c = await NextAsync(ct);
                    if (c == -1) return false;
                    if (emit) await output.WriteAsync((char)c, ct);
                    if (inString)
                    {
                        if (c == '\\')
                        {
                            var escaped = await NextAsync(ct);
                            if (escaped == -1) return false;
                            if (emit) await output.WriteAsync((char)escaped, ct);
                        }
                        else if (c == '"')
                        {
                            inString = false;
                        }
                        continue;
                    }
                    switch (c)
                    {
                        case '"': inString = true; break;
                        case '{': closers.Push('}'); break;
                        case '[': closers.Push(']'); break;
                        case '}' or ']':
                            if (closers.Count == 0 || closers.Pop() != c) return false;
                            break;
                    }
                } while (inString || closers.Count > 0);
                return true;
            }

            var length = 0;
            while (await PeekAsync(ct) is var c && c != -1 && c is not (',' or '}' or ']') && !char.IsWhiteSpace((char)c))
            {
                await NextAsync(ct);
                if (emit) await output.WriteAsync((char)c, ct);
                length++;
            }
            return length > 0;
        }

        /// <summary>A key's text, after its opening quote; escapes are kept as written. Null when it never ends.</summary>
        private async Task<string?> ReadKeyAsync(CancellationToken ct)
        {
            var key = new StringBuilder();
            while (true)
            {
                var c = await NextAsync(ct);
                if (c == -1) return null;
                if (c == '"') return key.ToString();
                key.Append((char)c);
                if (c == '\\')
                {
                    var escaped = await NextAsync(ct);
                    if (escaped == -1) return null;
                    key.Append((char)escaped);
                }
            }
        }

        private async ValueTask<int> PeekNonSpaceAsync(CancellationToken ct)
        {
            while (await PeekAsync(ct) is var c && c != -1 && char.IsWhiteSpace((char)c))
                await NextAsync(ct);
            return await PeekAsync(ct);
        }

        private async ValueTask<int> NextNonSpaceAsync(CancellationToken ct)
        {
            await PeekNonSpaceAsync(ct);
            return await NextAsync(ct);
        }

        private async ValueTask<int> PeekAsync(CancellationToken ct)
        {
            if (_position == _length && !await FillAsync(ct)) return -1;
            return _buffer[_position];
        }

        private async ValueTask<int> NextAsync(CancellationToken ct)
        {
            if (_position == _length && !await FillAsync(ct)) return -1;
            return _buffer[_position++];
        }

        private async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            _length = await text.ReadAsync(_buffer, ct);
            _position = 0;
            return _length > 0;
        }
    }
}
