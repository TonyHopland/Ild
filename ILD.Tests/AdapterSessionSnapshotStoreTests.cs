using System.Text;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// A session transcript is stored as metadata plus compressed chunks that each end on a
/// line: a turn appends only what it added, a rewrite replaces, and reads fetch only the
/// chunks they need.
/// </summary>
public sealed class AdapterSessionSnapshotStoreTests : IAsyncLifetime
{
    private const int MiB = 1024 * 1024;
    private const int ChunkTarget = 4 * MiB;

    private readonly SqliteConnection _connection = SqliteSchemaTemplate<AppDbContext>.OpenCopy(options => new AppDbContext(options));
    private readonly SnapshotSqlRecorder _sql = new();
    private readonly Guid _runId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SessionSnapshotKey Key(string sessionId = "sess-1", string adapter = "ClaudeCode")
        => SessionSnapshotKey.ForRun(_runId, adapter, sessionId);

    public async ValueTask InitializeAsync()
    {
        await using var db = Context();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"template-{_runId:N}", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
        db.LoopRuns.Add(new LoopRun
        {
            Id = _runId,
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        });
        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Three_appends_make_three_contiguous_chunks_that_read_back_byte_identical()
    {
        var turns = new[]
        {
            "{\"turn\":1,\"text\":\"first\"}\n{\"turn\":1,\"text\":\"reply\"}\n",
            "{\"turn\":2,\"text\":\"second\"}\n",
            "{\"turn\":3,\"text\":\"third\"}\n{\"turn\":3,\"text\":\"partial",
        };
        var all = Encoding.UTF8.GetBytes(string.Concat(turns));

        long stored = 0;
        byte[][] dataAfterTurnTwo = [];
        foreach (var (turn, index) in turns.Select((t, i) => (t, i)))
        {
            var result = await AppendAsync(Key(), turn, stored);
            Assert.Equal(SessionSnapshotAppendResult.Appended, result);
            stored += Encoding.UTF8.GetByteCount(turn);
            if (index == 1) dataAfterTurnTwo = (await ChunksAsync()).Select(c => c.Data).ToArray();
        }

        var chunks = await ChunksAsync();
        Assert.Equal(new[] { 0, 1, 2 }, chunks.Select(c => c.Seq));
        long offset = 0;
        foreach (var (chunk, turn) in chunks.Zip(turns))
        {
            Assert.Equal(offset, chunk.RawOffset);
            Assert.Equal(Encoding.UTF8.GetByteCount(turn), chunk.RawLength);
            offset += chunk.RawLength;
        }
        Assert.Equal(dataAfterTurnTwo[0], chunks[0].Data);
        Assert.Equal(dataAfterTurnTwo[1], chunks[1].Data);

        var meta = await Store().GetMetadataAsync(Key(), Ct);
        Assert.NotNull(meta);
        Assert.Equal(all.Length, meta.ByteLength);
        Assert.Equal(3, meta.ChunkCount);
        Assert.Equal(all.Count(b => b == (byte)'\n'), meta.EventCount);
        Assert.Equal("claude-jsonl", meta.Format);
        Assert.False(string.IsNullOrEmpty(meta.Compression));
        Assert.False(meta.Unconverted);

        Assert.Equal(all, await SessionSnapshotTestSupport.ReadAllAsync(Store(), Key(), Ct));
        var from = Encoding.UTF8.GetByteCount(turns[0]) + 3;
        Assert.Equal(all[from..], await ReadFromAsync(Key(), from));
    }

    [Fact]
    public async Task Replace_cuts_chunks_on_lines_and_compresses_a_large_transcript()
    {
        var oversizedLine = "{\"type\":\"tool_result\",\"content\":\"" + string.Concat(Enumerable.Repeat("output of the build step ", 5 * MiB / 25)) + "\"}\n";
        var raw = Encoding.UTF8.GetBytes(
            SyntheticTranscript.Lines("sess-1", 6 * MiB, seed: 3) + oversizedLine + SyntheticTranscript.Lines("sess-1", 3 * MiB, seed: 4));

        await SessionSnapshotTestSupport.SeedAsync(Store(), Key(), "claude-jsonl", raw, Ct);

        var chunks = await ChunksAsync();
        Assert.True(chunks.Count >= 4);
        AssertCutOnLines(chunks, raw, inputEndsInNewline: true);
        Assert.True(chunks.Sum(c => (long)c.Data.Length) <= raw.Length * 0.30,
            $"stored {chunks.Sum(c => (long)c.Data.Length)} bytes for {raw.Length} raw");
        Assert.Equal(raw, await SessionSnapshotTestSupport.ReadAllAsync(Store(), Key(), Ct));
    }

    [Fact]
    public async Task A_write_that_does_not_end_on_a_line_keeps_its_unterminated_tail_in_the_last_chunk()
    {
        var raw = Encoding.UTF8.GetBytes(SyntheticTranscript.Lines("sess-1", 5 * MiB, seed: 5) + "{\"unterminated\":");

        await SessionSnapshotTestSupport.SeedAsync(Store(), Key(), "claude-jsonl", raw, Ct);

        AssertCutOnLines(await ChunksAsync(), raw, inputEndsInNewline: false);
        Assert.Equal(raw, await SessionSnapshotTestSupport.ReadAllAsync(Store(), Key(), Ct));
    }

    [Fact]
    public async Task An_append_whose_expected_length_is_stale_is_a_conflict_and_writes_nothing()
    {
        await AppendAsync(Key(), "{\"a\":1}\n", 0);
        var before = await ChunksAsync();

        Assert.Equal(SessionSnapshotAppendResult.Conflict, await AppendAsync(Key(), "{\"b\":2}\n", 3));
        Assert.Equal(SessionSnapshotAppendResult.Conflict, await AppendAsync(Key(), "{\"b\":2}\n", 0));

        AssertSameChunks(before, await ChunksAsync());
        Assert.Equal(8, (await Store().GetMetadataAsync(Key(), Ct))!.ByteLength);
        Assert.Equal("{\"a\":1}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), Key(), Ct));
    }

    /// <summary>
    /// The two appends run at once, each on its own connection to one database file. SQLite
    /// serialises their transactions on its database write lock, where Postgres serialises them
    /// on the row lock the claim takes; either way the second sees the first's length and lands
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Of_two_appends_started_together_from_the_same_length_only_one_lands()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ild-snapshot-race-{Guid.NewGuid():N}.db");
        AppDbContext FileContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={file};Pooling=False")
            .WithPostgresSemantics()
            .Options);
        try
        {
            await using (var db = FileContext())
            {
                await db.Database.EnsureCreatedAsync(Ct);
                var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"template-{_runId:N}", RecoveryPolicy = RecoveryPolicy.AutoResume };
                var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
                db.LoopTemplates.Add(template);
                db.LoopTemplateVersions.Add(version);
                db.LoopRuns.Add(new LoopRun
                {
                    Id = _runId,
                    WorkItemId = Guid.NewGuid().ToString(),
                    LoopTemplateVersionId = version.Id,
                    Status = LoopRunStatus.Running,
                    RecoveryPolicy = RecoveryPolicy.AutoResume,
                });
                await db.SaveChangesAsync(Ct);
            }
            Task<SessionSnapshotAppendResult> Append(string text, long expected)
            {
                var store = new AdapterSessionSnapshotStore(FileContext());
                return store.AppendAsync(Key(), "claude-jsonl", new MemoryStream(Encoding.UTF8.GetBytes(text)), expected, "tail", Ct);
            }
            await Append("{\"base\":0}\n", 0);
            const string one = "{\"writer\":\"one\"}\n", two = "{\"writer\":\"two\"}\n";

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(async () => { await start.Task; return await Append(one, 11); }, Ct);
            var second = Task.Run(async () => { await start.Task; return await Append(two, 11); }, Ct);
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Equal(new[] { SessionSnapshotAppendResult.Appended, SessionSnapshotAppendResult.Conflict }, results.Order());
            var winner = results[0] == SessionSnapshotAppendResult.Appended ? one : two;
            Assert.Equal("{\"base\":0}\n" + winner, await SessionSnapshotTestSupport.ReadAllTextAsync(new AdapterSessionSnapshotStore(FileContext()), Key(), Ct));
            await using var check = FileContext();
            Assert.Equal(2, await check.AdapterSessionSnapshotChunks.CountAsync(Ct));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task An_append_to_a_missing_snapshot_creates_it_only_from_length_zero()
    {
        Assert.Equal(SessionSnapshotAppendResult.Conflict, await AppendAsync(Key("absent"), "{\"a\":1}\n", 5));
        Assert.Null(await Store().GetMetadataAsync(Key("absent"), Ct));

        Assert.Equal(SessionSnapshotAppendResult.Appended, await AppendAsync(Key("new"), "{\"a\":1}\n", 0));
        Assert.Equal("{\"a\":1}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), Key("new"), Ct));
    }

    [Fact]
    public async Task An_append_to_a_row_not_yet_converted_is_a_conflict_and_adds_no_chunk()
    {
        await using var db = Context();
        var id = await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "Pi", "sess-1", "{\"old\":1}\n", Ct);

        Assert.Equal(SessionSnapshotAppendResult.Conflict, await AppendAsync(Key("sess-1", "Pi"), "{\"new\":2}\n", 0));

        Assert.Empty(await ChunksAsync());
        Assert.Equal("{\"old\":1}\n", await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), id, Ct));
    }

    [Fact]
    public async Task Replace_leaves_only_the_new_content()
    {
        await AppendAsync(Key(), "{\"a\":1}\n", 0);
        await AppendAsync(Key(), "{\"b\":2}\n", 8);
        await AppendAsync(Key(), "{\"c\":3}\n", 16);

        await SessionSnapshotTestSupport.SeedAsync(Store(), Key(), "claude-jsonl", "{\"z\":26}\n{\"y\":25}\n", Ct);

        var chunks = await ChunksAsync();
        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.Seq);
        Assert.Equal(0, chunk.RawOffset);
        var meta = (await Store().GetMetadataAsync(Key(), Ct))!;
        Assert.Equal(18, meta.ByteLength);
        Assert.Equal(1, meta.ChunkCount);
        Assert.Equal(2, meta.EventCount);
        Assert.Equal("{\"z\":26}\n{\"y\":25}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), Key(), Ct));
        Assert.Equal(SessionSnapshotAppendResult.Appended, await AppendAsync(Key(), "{\"x\":24}\n", 18));
    }

    [Fact]
    public async Task Replace_of_a_row_not_yet_converted_clears_the_legacy_content()
    {
        await using var db = Context();
        var id = await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "Pi", "sess-1", "{\"old\":1}\n", Ct);

        await SessionSnapshotTestSupport.SeedAsync(Store(), Key("sess-1", "Pi"), "pi-jsonl", "{\"new\":2}\n", Ct);

        Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), id, Ct));
        Assert.Equal("{\"new\":2}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), Key("sess-1", "Pi"), Ct));
        Assert.Equal("pi-jsonl", (await Store().GetMetadataAsync(Key("sess-1", "Pi"), Ct))!.Format);
    }

    [Fact]
    public async Task A_replace_that_fails_midway_keeps_the_previous_content()
    {
        var previous = Encoding.UTF8.GetBytes(SyntheticTranscript.Lines("sess-1", 5 * MiB, seed: 6));
        await SessionSnapshotTestSupport.SeedAsync(Store(), Key(), "claude-jsonl", previous, Ct);
        var before = await ChunksAsync();
        var metaBefore = await Store().GetMetadataAsync(Key(), Ct);

        var inserts = 0;
        _sql.FailWhen = command => command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            && SnapshotSqlRecorder.TouchesChunks(command.CommandText)
            && ++inserts == 2;
        var replacement = Encoding.UTF8.GetBytes(SyntheticTranscript.Lines("sess-1", 9 * MiB, seed: 7));
        await Assert.ThrowsAnyAsync<Exception>(
            () => SessionSnapshotTestSupport.SeedAsync(Store(), Key(), "claude-jsonl", replacement, Ct));
        _sql.FailWhen = null;

        AssertSameChunks(before, await ChunksAsync());
        Assert.Equal(metaBefore, await Store().GetMetadataAsync(Key(), Ct));
        Assert.Equal(previous, await SessionSnapshotTestSupport.ReadAllAsync(Store(), Key(), Ct));
    }

    [Fact]
    public async Task Fork_writes_the_destination_id_into_every_chunk_and_leaves_the_source_alone()
    {
        const string source = "0f8c2a41-source-session";
        const string dest = "dest-session-with-a-much-longer-id";
        var raw = SyntheticTranscript.Lines(source, 9 * MiB, seed: 8);
        await SessionSnapshotTestSupport.SeedAsync(Store(), Key(source), "claude-jsonl", raw, Ct);
        await AppendAsync(Key(source), $"{{\"sessionId\":\"{source}\",\"turn\":\"last\"}}\n", Encoding.UTF8.GetByteCount(raw));
        var sourceRaw = await SessionSnapshotTestSupport.ReadAllAsync(Store(), Key(source), Ct);
        var sourceChunks = await ChunksAsync(Key(source));
        var sourceMeta = await Store().GetMetadataAsync(Key(source), Ct);

        Assert.True(await Store().ForkAsync(Key(source), Key(dest), Ct));

        var destRaw = (await SessionSnapshotTestSupport.ReadAllAsync(Store(), Key(dest), Ct))!;
        var expected = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(sourceRaw!).Replace(source, dest, StringComparison.Ordinal));
        Assert.Equal(expected, destRaw);
        var destChunks = await ChunksAsync(Key(dest));
        Assert.True(destChunks.Count >= 3);
        AssertCutOnLines(destChunks, destRaw, inputEndsInNewline: true);
        foreach (var chunk in destChunks)
            Assert.Contains(dest, Encoding.UTF8.GetString(destRaw, (int)chunk.RawOffset, chunk.RawLength), StringComparison.Ordinal);
        var destMeta = (await Store().GetMetadataAsync(Key(dest), Ct))!;
        Assert.Equal(destRaw.Length, destMeta.ByteLength);
        Assert.Equal("claude-jsonl", destMeta.Format);

        Assert.Equal(sourceRaw, await SessionSnapshotTestSupport.ReadAllAsync(Store(), Key(source), Ct));
        AssertSameChunks(sourceChunks, await ChunksAsync(Key(source)));
        Assert.Equal(sourceMeta, await Store().GetMetadataAsync(Key(source), Ct));
    }

    [Fact]
    public async Task Fork_of_a_missing_source_writes_nothing()
    {
        Assert.False(await Store().ForkAsync(Key("absent"), Key("dest"), Ct));

        Assert.Null(await Store().GetMetadataAsync(Key("dest"), Ct));
        Assert.Empty(await ChunksAsync());
    }

    [Fact]
    public async Task Fork_of_a_row_not_yet_converted_converts_it_first()
    {
        await using var db = Context();
        var id = await SessionSnapshotTestSupport.SeedLegacyAsync(
            db, _runId, null, "Pi", "src", "{\"sessionId\":\"src\",\"n\":1}\n{\"sessionId\":\"src\",\"n\":2}\n", Ct);

        Assert.True(await Store().ForkAsync(Key("src", "Pi"), Key("dst", "Pi"), Ct));

        Assert.Equal("{\"sessionId\":\"dst\",\"n\":1}\n{\"sessionId\":\"dst\",\"n\":2}\n",
            await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), Key("dst", "Pi"), Ct));
        Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), id, Ct));
        Assert.Equal("{\"sessionId\":\"src\",\"n\":1}\n{\"sessionId\":\"src\",\"n\":2}\n",
            await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), Key("src", "Pi"), Ct));
    }

    [Fact]
    public async Task Forking_a_snapshot_onto_itself_writes_nothing_and_converts_nothing()
    {
        await using var db = Context();
        var id = await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "Pi", "same", "{\"n\":1}\n", Ct);
        _sql.Clear();

        Assert.True(await Store().ForkAsync(Key("same", "Pi"), Key("same", "Pi"), Ct));

        Assert.DoesNotContain(_sql.Commands, c => !c.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("{\"n\":1}\n", await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), id, Ct));
    }

    [Fact]
    public async Task Listing_metadata_reads_no_chunk_and_no_legacy_content()
    {
        await SessionSnapshotTestSupport.SeedAsync(Store(), Key("big"), "claude-jsonl", SyntheticTranscript.Lines("big", 5 * MiB, seed: 9), Ct);
        await using (var db = Context())
            await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "Pi", "legacy", "{\"old\":1}\n", Ct);
        var chatId = await SeedChatAsync();
        await SessionSnapshotTestSupport.SeedAsync(Store(), SessionSnapshotKey.ForChat(chatId, "ClaudeCode", "chat"), "claude-jsonl", "{\"c\":1}\n", Ct);
        _sql.Clear();

        var forRun = await Store().ListMetadataForRunAsync(_runId, Ct);

        Assert.DoesNotContain(_sql.Commands, c => SnapshotSqlRecorder.TouchesChunks(c.Sql));
        Assert.DoesNotContain(_sql.Commands, c => SnapshotSqlRecorder.SelectsSessionJson(c.Sql));
        Assert.Equal(new[] { "big", "legacy" }, forRun.Select(m => m.SessionId).Order());
        var big = forRun.Single(m => m.SessionId == "big");
        Assert.False(big.Unconverted);
        Assert.True(big.ChunkCount >= 2);
        Assert.True(forRun.Single(m => m.SessionId == "legacy").Unconverted);
        Assert.Empty(await ChunksAsync(Key("legacy", "Pi")));
    }

    [Fact]
    public async Task A_head_read_fetches_only_the_chunks_its_page_reaches_and_says_when_the_content_goes_on()
    {
        const int budget = 1024 * 1024;
        var parts = new[] { new string('a', 600 * 1024), new string('b', 600 * 1024), new string('c', 600 * 1024) };
        long stored = 0;
        foreach (var part in parts)
        {
            await AppendAsync(Key(), part, stored);
            stored += part.Length;
        }

        _sql.Clear();
        var head = (await Store().ReadHeadAsync(Key(), Ct))!;
        Assert.Equal(string.Concat(parts)[..budget], Assert.Single(head.Events));
        Assert.True(head.Truncated);
        Assert.Equal(0, head.FromOffset);
        Assert.Null(head.PreviousOffset);
        Assert.Equal(2, _sql.ChunkDataReads);

        await SessionSnapshotTestSupport.SeedAsync(Store(), Key("small"), "opencode-json", "{\n  \"id\": \"small\"\n}\n", Ct);
        var whole = (await Store().ReadHeadAsync(Key("small"), Ct))!;
        Assert.Equal("{\n  \"id\": \"small\"\n}\n", Assert.Single(whole.Events));
        Assert.False(whole.Truncated);

        await Store().ReplaceAsync(Key("empty"), "opencode-json", new MemoryStream(), Ct);
        var empty = (await Store().ReadHeadAsync(Key("empty"), Ct))!;
        Assert.Empty(empty.Events);
        Assert.False(empty.Truncated);
        Assert.Equal(0, empty.Snapshot.ByteLength);

        Assert.Null(await Store().ReadHeadAsync(Key("missing"), Ct));
        Assert.Null(await Store().ReadTailAsync(Key("missing"), 10, null, Ct));
    }

    [Fact]
    public async Task A_tail_read_fetches_only_the_trailing_chunks_it_needs()
    {
        var turns = new[]
        {
            "{\"n\":0}\n{\"n\":1}\n{\"n\":2}\n",
            "{\"n\":3}\n{\"n\":4}\n{\"n\":5}\n",
            "{\"n\":6}\n{\"n\":7}\n{\"n\":8}\n",
        };
        long stored = 0;
        foreach (var turn in turns)
        {
            await AppendAsync(Key(), turn, stored);
            stored += turn.Length;
        }

        _sql.Clear();
        var lastTwo = (await Store().ReadTailAsync(Key(), 2, null, Ct))!;
        Assert.Equal(new[] { "{\"n\":7}", "{\"n\":8}" }, lastTwo.Events);
        Assert.Equal(7 * 8, lastTwo.FromOffset);
        Assert.Equal(1, _sql.ChunkDataReads);

        _sql.Clear();
        var lastFour = (await Store().ReadTailAsync(Key(), 4, null, Ct))!;
        Assert.Equal(new[] { "{\"n\":5}", "{\"n\":6}", "{\"n\":7}", "{\"n\":8}" }, lastFour.Events);
        Assert.Equal(5 * 8, lastFour.FromOffset);
        Assert.Equal(2, _sql.ChunkDataReads);
    }

    [Fact]
    public async Task Deleting_a_snapshot_or_its_run_removes_its_chunks()
    {
        await SessionSnapshotTestSupport.SeedAsync(Store(), Key("one"), "claude-jsonl", "{\"a\":1}\n", Ct);
        await SessionSnapshotTestSupport.SeedAsync(Store(), Key("two"), "claude-jsonl", "{\"b\":2}\n", Ct);
        var one = (await Store().GetMetadataAsync(Key("one"), Ct))!.Id;

        await using (var db = Context())
            await db.AdapterSessionSnapshots.Where(s => s.Id == one).ExecuteDeleteAsync(Ct);
        Assert.Empty(await ChunksAsync(Key("one")));
        Assert.Single(await ChunksAsync(Key("two")));

        await using (var db = Context())
            await db.LoopRuns.Where(r => r.Id == _runId).ExecuteDeleteAsync(Ct);
        await using (var db = Context())
            Assert.Empty(await db.AdapterSessionSnapshotChunks.ToListAsync(Ct));
    }

    [Fact]
    public async Task Deleting_a_runs_snapshots_removes_them_with_their_chunks_and_leaves_other_owners_alone()
    {
        Guid otherRunId;
        await using (var db = Context())
        {
            var versionId = await db.LoopRuns.Where(r => r.Id == _runId).Select(r => r.LoopTemplateVersionId).SingleAsync(Ct);
            otherRunId = Guid.NewGuid();
            db.LoopRuns.Add(new LoopRun
            {
                Id = otherRunId,
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = versionId,
                Status = LoopRunStatus.Completed,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
            });
            await db.SaveChangesAsync(Ct);
        }
        var chatId = await SeedChatAsync();
        await SessionSnapshotTestSupport.SeedAsync(Store(), Key("one"), "claude-jsonl", "{\"a\":1}\n", Ct);
        await SessionSnapshotTestSupport.SeedAsync(Store(), Key("two", "Codex"), "claude-jsonl", "{\"b\":2}\n", Ct);
        var otherRun = SessionSnapshotKey.ForRun(otherRunId, "ClaudeCode", "sess-1");
        var chat = SessionSnapshotKey.ForChat(chatId, "ClaudeCode", "sess-1");
        await SessionSnapshotTestSupport.SeedAsync(Store(), otherRun, "claude-jsonl", "{\"c\":3}\n", Ct);
        await SessionSnapshotTestSupport.SeedAsync(Store(), chat, "claude-jsonl", "{\"d\":4}\n", Ct);

        Assert.Equal(2, await Store().DeleteForRunAsync(_runId, Ct));

        Assert.Empty(await Store().ListMetadataForRunAsync(_runId, Ct));
        Assert.Empty(await ChunksAsync(Key("one")));
        Assert.Empty(await ChunksAsync(Key("two", "Codex")));
        Assert.Equal("{\"c\":3}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), otherRun, Ct));
        Assert.Equal("{\"d\":4}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(Store(), chat, Ct));
        await using (var db = Context())
            Assert.Equal(2, await db.AdapterSessionSnapshotChunks.CountAsync(Ct));

        Assert.Equal(0, await Store().DeleteForRunAsync(_runId, Ct));
    }

    private AppDbContext Context()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .WithPostgresSemantics()
            .AddInterceptors(_sql)
            .Options);

    private IAdapterSessionSnapshotStore Store() => new AdapterSessionSnapshotStore(Context());

    private async Task<SessionSnapshotAppendResult> AppendAsync(SessionSnapshotKey key, string text, long expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await Store().AppendAsync(key, "claude-jsonl", stream, expected, $"tail-{expected}-{text.Length}", Ct);
    }

    private async Task<byte[]> ReadFromAsync(SessionSnapshotKey key, long offset)
    {
        await using var stream = await Store().OpenReadAsync(key, offset, Ct);
        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        return copy.ToArray();
    }

    private async Task<List<AdapterSessionSnapshotChunk>> ChunksAsync(SessionSnapshotKey? key = null)
    {
        key ??= Key();
        await using var db = Context();
        var ids = await db.AdapterSessionSnapshots
            .Where(s => s.LoopRunId == key.LoopRunId && s.AdapterName == key.AdapterName && s.SessionId == key.SessionId)
            .Select(s => s.Id)
            .ToListAsync(Ct);
        return await db.AdapterSessionSnapshotChunks.AsNoTracking()
            .Where(c => ids.Contains(c.SnapshotId))
            .OrderBy(c => c.Seq)
            .ToListAsync(Ct);
    }

    private async Task<Guid> SeedChatAsync()
    {
        await using var db = Context();
        var providerId = Guid.NewGuid();
        db.AiProviders.Add(new AiProvider { Id = providerId, Name = $"provider-{providerId:N}", Type = "claude-code", BaseUrl = string.Empty, Model = string.Empty });
        var chat = new ChatSession { Id = Guid.NewGuid(), UserId = "user", AiProviderId = providerId, ProviderType = "claude-code" };
        db.ChatSessions.Add(chat);
        await db.SaveChangesAsync(Ct);
        return chat.Id;
    }

    private static void AssertSameChunks(IReadOnlyList<AdapterSessionSnapshotChunk> expected, IReadOnlyList<AdapterSessionSnapshotChunk> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (e, a) in expected.Zip(actual))
        {
            Assert.Equal((e.Seq, e.RawOffset, e.RawLength), (a.Seq, a.RawOffset, a.RawLength));
            Assert.Equal(e.Data, a.Data);
        }
    }

    /// <summary>
    /// Chunks tile the content, each ending right after a newline unless it is the last one
    /// of an input without a final newline, and outgrow the target only to hold a single line.
    /// </summary>
    private static void AssertCutOnLines(IReadOnlyList<AdapterSessionSnapshotChunk> chunks, byte[] raw, bool inputEndsInNewline)
    {
        long offset = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Assert.Equal(i, chunk.Seq);
            Assert.Equal(offset, chunk.RawOffset);
            var slice = raw.AsSpan((int)chunk.RawOffset, chunk.RawLength);
            var last = i == chunks.Count - 1;
            if (!last || inputEndsInNewline)
                Assert.True(slice[^1] == (byte)'\n', $"chunk {i} does not end on a line");
            if (chunk.RawLength > ChunkTarget)
                Assert.True(slice[..^1].IndexOf((byte)'\n') < 0, $"chunk {i} is {chunk.RawLength} bytes and holds more than one line");
            offset += chunk.RawLength;
        }
        Assert.Equal(raw.Length, offset);
    }
}
