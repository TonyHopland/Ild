using System.Data.Common;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

/// <summary>
/// Rows written before the split hold their whole transcript in the legacy
/// <c>SessionJson</c> column. The startup migration turns each into chunks one row at a
/// time; a row it has not reached yet is converted by whichever read needs its content.
/// </summary>
public sealed class SessionSnapshotContentMigratorTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = SqliteSchemaTemplate<AppDbContext>.OpenCopy(options => new AppDbContext(options));
    private readonly SnapshotSqlRecorder _sql = new();
    private readonly ListLogger<SessionSnapshotMigrationService> _log = new();
    private readonly Guid _runId = Guid.NewGuid();
    private ServiceProvider _services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection).WithPostgresSemantics().AddInterceptors(_sql));
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        _services = services.BuildServiceProvider();

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

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private const string TrickyEventA = """{"type":"user","sessionId":"s1","message":{"content":"a \"quoted\" ], } and \\ and \"events\":[1]"}}""";
    private const string TrickyEventB = """{ "type" : "assistant", "events" : [ {"nested":true}, [1,2] ], "text":"café – ünïcode ✓" }""";

    public static TheoryData<string, string, string, string> LegacyRows => new()
    {
        {
            "ClaudeCode",
            SessionSnapshotTestSupport.LegacyClaudeWrapper("s1", TrickyEventA, TrickyEventB, "{}"),
            TrickyEventA + "\n" + TrickyEventB + "\n" + "{}\n",
            "claude-jsonl"
        },
        { "ClaudeCode", """{"format":"claude-jsonl","sessionId":"s1","events":[]}""", "", "claude-jsonl" },
        { "ClaudeCode", """{"format":"claude-jsonl","events":[{"truncated":""", """{"format":"claude-jsonl","events":[{"truncated":""", "raw" },
        { "ClaudeCode", "{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n", "{\"type\":\"user\"}\n{\"type\":\"assistant\"}\n", "raw" },
        { "Pi", "{\"type\":\"session\",\"id\":\"s1\"}\n{\"type\":\"message\"}\n", "{\"type\":\"session\",\"id\":\"s1\"}\n{\"type\":\"message\"}\n", "pi-jsonl" },
        { "Codex", "{\"type\":\"session_meta\"}\n{\"type\":\"event_msg\"}", "{\"type\":\"session_meta\"}\n{\"type\":\"event_msg\"}", "codex-jsonl" },
        { "OpenCode", """{"info":{"id":"s1"},"messages":[{"role":"user"}]}""", """{"info":{"id":"s1"},"messages":[{"role":"user"}]}""", "opencode-json" },
    };

    [Theory]
    [MemberData(nameof(LegacyRows))]
    public async Task A_legacy_row_becomes_chunks_and_loses_its_legacy_content(string adapter, string sessionJson, string expectedRaw, string expectedFormat)
    {
        Guid id;
        await using (var db = Context())
            id = await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, adapter, "s1", sessionJson, Ct);

        await RunMigrationAsync();

        Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), id, Ct));
        var key = SessionSnapshotKey.ForRun(_runId, adapter, "s1");
        var meta = await StoreMetadataAsync(key);
        Assert.Equal(expectedFormat, meta.Format);
        Assert.False(meta.Unconverted);
        var expected = Encoding.UTF8.GetBytes(expectedRaw);
        Assert.Equal(expected.Length, meta.ByteLength);
        Assert.Equal(expected.Count(b => b == (byte)'\n'), meta.EventCount);
        Assert.Equal(expected, await ReadAllAsync(key));
    }

    [Fact]
    public async Task A_large_claude_row_converts_into_line_cut_chunks_and_a_second_pass_changes_nothing()
    {
        var lines = SyntheticTranscript.Lines("s1", 9 * 1024 * 1024, seed: 11).TrimEnd('\n').Split('\n');
        await using (var db = Context())
            await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "ClaudeCode", "s1", SessionSnapshotTestSupport.LegacyClaudeWrapper("s1", lines), Ct);
        var expected = Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n")));

        await RunMigrationAsync();
        var key = SessionSnapshotKey.ForRun(_runId, "ClaudeCode", "s1");
        var chunks = await ChunksAsync();
        var meta = await StoreMetadataAsync(key);

        Assert.True(chunks.Count >= 3);
        long offset = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(offset, chunk.RawOffset);
            Assert.Equal((byte)'\n', expected[chunk.RawOffset + chunk.RawLength - 1]);
            offset += chunk.RawLength;
        }
        Assert.Equal(expected.Length, offset);
        Assert.Equal(expected, await ReadAllAsync(key));

        _sql.Clear();
        await RunMigrationAsync();

        Assert.DoesNotContain(_sql.Commands, c => SnapshotSqlRecorder.TouchesChunks(c.Sql));
        var again = await ChunksAsync();
        Assert.Equal(chunks.Select(c => (c.Seq, c.RawOffset, c.RawLength)), again.Select(c => (c.Seq, c.RawOffset, c.RawLength)));
        Assert.All(chunks.Zip(again), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
        Assert.Equal(meta, await StoreMetadataAsync(key));
    }

    [Fact]
    public async Task A_row_that_fails_is_logged_and_left_for_the_next_start_while_the_rest_convert()
    {
        Guid failing, other;
        await using (var db = Context())
        {
            failing = await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "Pi", "fails", "{\"n\":1}\n", Ct);
            other = await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "Pi", "converts", "{\"n\":2}\n", Ct);
        }

        _sql.FailWhen = command => IsWrite(command) && Mentions(command, failing);
        await RunMigrationAsync();
        _sql.FailWhen = null;

        Assert.Contains(_log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal("{\"n\":1}\n", await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), failing, Ct));
        Assert.Empty(await ChunksAsync(failing));
        Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), other, Ct));
        Assert.Equal("{\"n\":2}\n", Encoding.UTF8.GetString((await ReadAllAsync(SessionSnapshotKey.ForRun(_runId, "Pi", "converts")))!));

        await RunMigrationAsync();

        Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), failing, Ct));
        Assert.Equal("{\"n\":1}\n", Encoding.UTF8.GetString((await ReadAllAsync(SessionSnapshotKey.ForRun(_runId, "Pi", "fails")))!));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("read")]
    [InlineData("tail")]
    [InlineData("range")]
    public async Task Reading_a_row_the_migration_has_not_reached_converts_it_first(string read)
    {
        Guid id;
        await using (var db = Context())
            id = await SessionSnapshotTestSupport.SeedLegacyAsync(
                db, _runId, null, "ClaudeCode", "s1", SessionSnapshotTestSupport.LegacyClaudeWrapper("s1", "{\"n\":1}", "{\"n\":2}"), Ct);
        var key = SessionSnapshotKey.ForRun(_runId, "ClaudeCode", "s1");

        await using (var scope = _services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
            switch (read)
            {
                case "metadata":
                    var meta = await store.GetMetadataAsync(key, Ct);
                    Assert.NotNull(meta);
                    Assert.False(meta.Unconverted);
                    Assert.Equal("claude-jsonl", meta.Format);
                    Assert.Equal(16, meta.ByteLength);
                    break;
                case "read":
                    Assert.Equal("{\"n\":1}\n{\"n\":2}\n", await SessionSnapshotTestSupport.ReadAllTextAsync(store, key, Ct));
                    break;
                case "tail":
                    Assert.Equal(new[] { "{\"n\":1}", "{\"n\":2}" }, (await store.ReadTailAsync(key, 10, Ct)).Events);
                    break;
                case "range":
                    Assert.Equal(new[] { "{\"n\":1}", "{\"n\":2}" }, (await store.ReadRangeAsync(key, 0, 10, Ct)).Events);
                    break;
            }
        }

        Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(Context(), id, Ct));
        _sql.Clear();
        await RunMigrationAsync();
        Assert.DoesNotContain(_sql.Commands, c => SnapshotSqlRecorder.TouchesChunks(c.Sql));
        Assert.Equal("{\"n\":1}\n{\"n\":2}\n", Encoding.UTF8.GetString((await ReadAllAsync(key))!));
    }

    private async Task RunMigrationAsync()
    {
        using var service = new SessionSnapshotMigrationService(_services.GetRequiredService<IServiceScopeFactory>(), _log);
        await service.StartAsync(Ct);
        await service.ExecuteTask!;
        await service.StopAsync(Ct);
    }

    private static bool IsWrite(DbCommand command)
    {
        var sql = command.CommandText.TrimStart();
        return sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("UPDATE \"AdapterSessionSnapshots\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Mentions(DbCommand command, Guid id)
        => command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid g ? g == id
            : string.Equals(p.Value?.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase));

    private async Task<SessionSnapshotMetadata> StoreMetadataAsync(SessionSnapshotKey key)
    {
        await using var scope = _services.CreateAsyncScope();
        var all = await scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>().ListMetadataForRunAsync(_runId, Ct);
        return Assert.Single(all, m => m.AdapterName == key.AdapterName && m.SessionId == key.SessionId);
    }

    private async Task<byte[]?> ReadAllAsync(SessionSnapshotKey key)
    {
        await using var scope = _services.CreateAsyncScope();
        return await SessionSnapshotTestSupport.ReadAllAsync(scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>(), key, Ct);
    }

    private async Task<List<AdapterSessionSnapshotChunk>> ChunksAsync(Guid? snapshotId = null)
    {
        await using var db = Context();
        return await db.AdapterSessionSnapshotChunks.AsNoTracking()
            .Where(c => snapshotId == null || c.SnapshotId == snapshotId)
            .OrderBy(c => c.SnapshotId).ThenBy(c => c.Seq)
            .ToListAsync(Ct);
    }

    private AppDbContext Context()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).WithPostgresSemantics().Options);
}
