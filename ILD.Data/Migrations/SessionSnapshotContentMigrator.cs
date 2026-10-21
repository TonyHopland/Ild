using System.Text;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// Moves a session snapshot written before the metadata/content split out of the legacy
/// <c>SessionJson</c> column into compressed chunks, one row at a time.
///
/// The legacy text is read a slice at a time and each chunk is written as soon as it is
/// cut, so neither is ever held whole: Claude Code's wrapper
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

    /// <summary>
    /// Converts one row in one transaction that claims it, writes each chunk as soon as it is
    /// cut and then empties the legacy column. The legacy text is read a slice at a time, so no
    /// more than one slice and one chunk are held. A Claude wrapper is unwrapped
    /// as it is read. False when the row holds no legacy content (any more).
    /// </summary>
    public static async Task<bool> ConvertRowAsync(AppDbContext db, Guid snapshotId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.AdapterSessionSnapshots
            .Where(s => s.Id == snapshotId && EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn) != null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Compression, SessionSnapshotFormats.Brotli), ct);
        if (claimed == 0) return false;

        var row = await db.AdapterSessionSnapshots
            .Where(s => s.Id == snapshotId)
            .Select(s => new { s.AdapterName, Length = EF.Property<string>(s, AdapterSessionSnapshot.LegacySessionJsonColumn).Length })
            .SingleAsync(ct);
        // A row never holds chunks beside legacy content; clearing any lets one that somehow
        // does convert cleanly instead of failing every read that converts it.
        await db.AdapterSessionSnapshotChunks.Where(c => c.SnapshotId == snapshotId).ExecuteDeleteAsync(ct);

        // A Claude wrapper is unwrapped as it is read, in the one pass that writes the chunks.
        // Content that turns out not to be one is rare; its chunks are dropped and it is
        // written again verbatim, as content the conversion could not interpret.
        var format = row.AdapterName switch
        {
            "ClaudeCode" => SessionSnapshotFormats.ClaudeJsonl,
            "Pi" => SessionSnapshotFormats.PiJsonl,
            "Codex" => SessionSnapshotFormats.CodexJsonl,
            "OpenCode" => SessionSnapshotFormats.OpenCodeJson,
            _ => SessionSnapshotFormats.Raw,
        };
        var chunker = await WriteAsync(db, snapshotId, row.Length, unwrap: format == SessionSnapshotFormats.ClaudeJsonl, ct);
        if (chunker is null)
        {
            await db.AdapterSessionSnapshotChunks.Where(c => c.SnapshotId == snapshotId).ExecuteDeleteAsync(ct);
            format = SessionSnapshotFormats.Raw;
            chunker = (await WriteAsync(db, snapshotId, row.Length, unwrap: false, ct))!;
        }

        await db.AdapterSessionSnapshots
            .Where(s => s.Id == snapshotId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Format, format)
                .SetProperty(s => s.ByteLength, chunker.BytesWritten)
                .SetProperty(s => s.ChunkCount, chunker.ChunksWritten)
                .SetProperty(s => s.EventCount, chunker.LinesWritten)
                .SetProperty(s => s.TailHash, chunker.TailHash)
                .SetProperty(s => EF.Property<string?>(s, AdapterSessionSnapshot.LegacySessionJsonColumn), (string?)null), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// The row's legacy text written as chunks, unwrapping a Claude wrapper when
    /// <paramref name="unwrap"/>; null when the text turned out not to be one.
    /// </summary>
    private static async Task<SessionSnapshotChunker?> WriteAsync(AppDbContext db, Guid snapshotId, int length, bool unwrap, CancellationToken ct)
    {
        var chunker = new SessionSnapshotChunker(snapshotId, 0, 0, (chunk, token) => InsertAsync(db, chunk, token));
        var output = new Utf8Output(chunker);
        var text = new LegacyText(db, snapshotId, length);
        if (unwrap)
        {
            if (!await new ClaudeWrapperScanner(text, output).TryUnwrapAsync(ct)) return null;
        }
        else
        {
            await output.CopyAsync(text, ct);
        }
        await output.FlushAsync(ct);
        await chunker.CompleteAsync(ct);
        return chunker;
    }

    private static async Task InsertAsync(AppDbContext db, AdapterSessionSnapshotChunk chunk, CancellationToken ct)
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

    /// <summary>
    /// A row's legacy text, fetched a slice of characters at a time by its own query, so no
    /// reader stays open between slices and other commands can run on the connection. Postgres
    /// decompresses a stored value up to the end of each slice it is asked for, so slices are
    /// large: an 85 MB transcript converts in about a dozen queries.
    /// </summary>
    private sealed class LegacyText(AppDbContext db, Guid snapshotId, int length) : TextReader
    {
        public const int SliceChars = 8 * 1024 * 1024;

        private string _slice = string.Empty;
        private int _position;
        private int _nextStart = 1;

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == _slice.Length)
            {
                if (_nextStart > length) return 0;
                var count = Math.Min(SliceChars, length - _nextStart + 1);
                var slices = await db.Database
                    .SqlQuery<string>($"""SELECT substr("SessionJson", {_nextStart}, {count}) AS "Value" FROM "AdapterSessionSnapshots" WHERE "Id" = {snapshotId}""")
                    .ToListAsync(cancellationToken);
                _slice = slices.Single();
                _position = 0;
                _nextStart += count;
                if (_slice.Length == 0) return 0;
            }

            var taken = Math.Min(buffer.Length, _slice.Length - _position);
            _slice.AsMemory(_position, taken).CopyTo(buffer);
            _position += taken;
            return taken;
        }
    }

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

        public async ValueTask WriteAsync(ReadOnlyMemory<char> text, CancellationToken ct)
        {
            while (!text.IsEmpty)
            {
                if (_count == _chars.Length) await EncodeAsync(flush: false, ct);
                var taken = Math.Min(text.Length, _chars.Length - _count);
                text[..taken].CopyTo(_chars.AsMemory(_count));
                _count += taken;
                text = text[taken..];
            }
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
                // Scanned a buffer at a time and copied out in runs: these values are nearly all
                // of a transcript, so they are the one place the scan must not go a character
                // per call.
                var closers = new Stack<char>();
                var inString = false;
                var escaped = false;
                while (true)
                {
                    if (_position == _length && !await FillAsync(ct)) return false;
                    var start = _position;
                    var done = false;
                    while (!done && _position < _length)
                    {
                        var c = _buffer[_position++];
                        if (inString)
                        {
                            if (escaped) escaped = false;
                            else if (c == '\\') escaped = true;
                            else if (c == '"') inString = false;
                        }
                        else if (c == '"')
                        {
                            inString = true;
                        }
                        else if (c is '{' or '[')
                        {
                            closers.Push(c == '{' ? '}' : ']');
                        }
                        else if (c is '}' or ']' && (closers.Count == 0 || closers.Pop() != c))
                        {
                            return false;
                        }
                        done = !inString && closers.Count == 0;
                    }
                    if (emit) await output.WriteAsync(_buffer.AsMemory(start, _position - start), ct);
                    if (done) return true;
                }
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
