using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// Claude keeps its session files under <c>$HOME/.claude/projects</c>, in the shared
/// credential store the agent can write. ILD restores and saves them only as the
/// agent: a link planted there is replaced or ignored, never followed, and a
/// planted directory never fails the run. The adapter is given its own <c>HOME</c>.
/// </summary>
public sealed class ClaudeCodeSessionFileTests : IAsyncLifetime
{
    private const string SessionId = "claude-sess";

    private readonly string _home = Directory.CreateTempSubdirectory("ild-claude-session-home-").FullName;
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-claude-session-wt-").FullName;
    private readonly Guid _runId = Guid.NewGuid();
    private readonly TestProcessEnvironment _environment = new();
    private readonly SnapshotSqlRecorder _sql = new();
    private readonly ListLogger<SessionSnapshotMigrationService> _migrationLog = new();
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    private string SessionPath => ClaudeCodeAdapter.GetSessionFilePath(
        _worktree, SessionId, AgentIsolation.AgentUser, AgentIsolation.AgentHome, _environment)!;

    public async ValueTask InitializeAsync()
    {
        _environment.Set("HOME", _home);

        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection).AddInterceptors(_sql));
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        _services = services.BuildServiceProvider();

        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
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
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        foreach (var dir in new[] { _home, _worktree })
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var nested in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetUnixFileMode(nested, File.GetUnixFileMode(nested) | UnixFileMode.UserWrite); } catch { }
                }
            }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Session_files_live_under_the_home_the_claude_child_runs_with()
    {
        var agentHome = Path.Combine(_home, "agent-home");
        var agentProjects = Path.Combine(agentHome, ".claude", "projects") + Path.DirectorySeparatorChar;
        var ownProjects = Path.Combine(_home, ".claude", "projects") + Path.DirectorySeparatorChar;

        Assert.StartsWith(agentProjects, ClaudeCodeAdapter.GetSessionFilePath(_worktree, SessionId, "agent", agentHome, _environment));
        // Isolation off, or no agent home configured: the crossing leaves HOME alone.
        Assert.StartsWith(ownProjects, ClaudeCodeAdapter.GetSessionFilePath(_worktree, SessionId, agentUser: null, agentHome, _environment));
        Assert.StartsWith(ownProjects, ClaudeCodeAdapter.GetSessionFilePath(_worktree, SessionId, "agent", agentHome: null, _environment));
    }

    [Fact]
    public async Task Restore_replaces_a_planted_link_without_touching_its_target()
    {
        await SeedSnapshotAsync("{\"session_id\":\"claude-sess\",\"type\":\"assistant\",\"text\":\"restored\"}\n");
        var victim = Path.Combine(_home, "orchestrator-owned.txt");
        File.WriteAllText(victim, "victim");
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.CreateSymbolicLink(SessionPath, victim);

        await RunAsync();

        Assert.Equal("victim", File.ReadAllText(victim));
        Assert.Null(new FileInfo(SessionPath).LinkTarget);
        Assert.Contains("restored", File.ReadAllText(SessionPath));
    }

    [Fact]
    public async Task Restore_replaces_a_planted_read_only_directory_and_the_run_goes_ahead()
    {
        await SeedSnapshotAsync("{\"session_id\":\"claude-sess\",\"type\":\"assistant\",\"text\":\"restored\"}\n");
        var locked = Directory.CreateDirectory(Path.Combine(SessionPath, "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "file"), "x");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await RunAsync();

        Assert.Contains("restored", File.ReadAllText(SessionPath));
    }

    [Fact]
    public async Task Persist_saves_the_session_file_claude_left()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"type\":\"assistant\",\"text\":\"saved\"}\n");

        await RunAsync();

        Assert.Equal(File.ReadAllBytes(SessionPath), await StoredAsync());
        Assert.Equal("claude-jsonl", (await MetadataAsync())?.Format);
    }

    [Fact]
    public async Task Persist_never_reads_through_a_planted_link()
    {
        var secret = Path.Combine(_home, "orchestrator-secret.txt");
        File.WriteAllText(secret, "{\"secret\":\"do-not-copy\"}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.CreateSymbolicLink(SessionPath, secret);

        await RunAsync();

        Assert.Null(await MetadataAsync());
    }

    [Fact]
    public async Task Each_turn_stores_only_what_the_session_file_gained_and_restore_brings_it_back_byte_identical()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":1,\"text\":\"café\"}\n");
        await RunAsync();
        var afterFirst = await ChunksAsync();

        File.AppendAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":2}\n{\"session_id\":\"claude-sess\",\"turn\":2,\"text\":\"more\"}\n");
        await RunAsync();
        var afterSecond = await ChunksAsync();

        Assert.Equal(afterFirst.Count + 1, afterSecond.Count);
        Assert.All(afterFirst.Zip(afterSecond), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
        Assert.Equal(File.ReadAllBytes(SessionPath), await StoredAsync());
        Assert.Equal(new FileInfo(SessionPath).Length, (await MetadataAsync())!.ByteLength);

        _sql.Clear();
        await RunAsync();
        Assert.DoesNotContain(_sql.Commands, c => WritesSnapshots(c.Sql));

        var original = File.ReadAllBytes(SessionPath);
        File.Delete(SessionPath);
        await RunAsync();
        Assert.Equal(original, File.ReadAllBytes(SessionPath));
        Assert.Equal(original, await StoredAsync());
    }

    [Theory]
    [InlineData("{\"session_id\":\"claude-sess\",\"turn\":1,\"text\":\"REWRITTEN first line\"}\n{\"session_id\":\"claude-sess\",\"turn\":1}\n{\"extra\":\"line making the file longer than before\"}\n")]
    [InlineData("{\"session_id\":\"claude-sess\",\"compacted\":true}\n")]
    public async Task A_rewritten_session_file_replaces_what_was_stored_and_later_turns_append_to_it(string rewritten)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":1,\"text\":\"original first line\"}\n{\"session_id\":\"claude-sess\",\"turn\":1}\n");
        await RunAsync();

        File.WriteAllText(SessionPath, rewritten);
        await RunAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(rewritten), await StoredAsync());
        var afterRewrite = await ChunksAsync();
        Assert.Equal(0, afterRewrite[0].RawOffset);

        File.AppendAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":3}\n");
        await RunAsync();
        Assert.Equal(afterRewrite.Count + 1, (await ChunksAsync()).Count);
        Assert.Equal(File.ReadAllBytes(SessionPath), await StoredAsync());
    }

    [Fact]
    public async Task A_turn_on_a_long_session_sends_less_than_a_megabyte_to_the_database()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, SyntheticTranscript.Lines(SessionId, 6 * 1024 * 1024, seed: 21));
        await RunAsync();

        File.AppendAllText(SessionPath, SyntheticTranscript.Lines(SessionId, 40 * 1024, seed: 22));
        _sql.Clear();
        await RunAsync();

        Assert.True(_sql.ParameterBytes < 1024 * 1024, $"the turn sent {_sql.ParameterBytes} bytes of parameters");
        Assert.Equal(File.ReadAllBytes(SessionPath), await StoredAsync());
    }

    [Fact]
    public async Task A_save_that_loses_a_race_to_another_writer_re_reads_and_still_stores_the_session_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":1}\n");
        await RunAsync();
        File.AppendAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":2}\n");

        // Another writer lands between this turn's metadata read and its append.
        var raced = 0;
        var racing = new ServiceCollection();
        racing.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        racing.AddScoped<IAdapterSessionSnapshotStore>(sp => new RacingSnapshotStore(
            new AdapterSessionSnapshotStore(sp.GetRequiredService<AppDbContext>()), async () =>
            {
                await AppendFromAnotherWriterAsync();
                Interlocked.Increment(ref raced);
            }));
        await using var racingServices = racing.BuildServiceProvider();

        await RunAsync(services: racingServices);

        Assert.Equal(1, raced);
        Assert.Equal(File.ReadAllBytes(SessionPath), await StoredAsync());
    }

    private async Task AppendFromAnotherWriterAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var stored = (await StoredAsync())!;
        var added = Encoding.UTF8.GetBytes("{\"session_id\":\"claude-sess\",\"writer\":\"another\"}\n");
        var whole = stored.Concat(added).ToArray();
        await using var db = CreateDbContext();
        var result = await new AdapterSessionSnapshotStore(db).AppendAsync(
            Key, SessionSnapshotFormats.ClaudeJsonl, new MemoryStream(added), stored.Length,
            SessionSnapshotFormats.TailHash(whole.AsSpan(Math.Max(0, whole.Length - SessionSnapshotFormats.TailWindow))), ct);
        Assert.Equal(SessionSnapshotAppendResult.Appended, result);
    }

    /// <summary>The real store, with <c>race</c> run once just before the first append reaches it.</summary>
    private sealed class RacingSnapshotStore(IAdapterSessionSnapshotStore inner, Func<Task> race) : IAdapterSessionSnapshotStore
    {
        private Func<Task>? _race = race;

        public async Task<SessionSnapshotAppendResult> AppendAsync(
            SessionSnapshotKey key, string format, Stream newRaw, long expectedByteLength, string newTailHash, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _race, null) is { } race) await race();
            return await inner.AppendAsync(key, format, newRaw, expectedByteLength, newTailHash, cancellationToken);
        }

        public Task<IReadOnlyList<SessionSnapshotMetadata>> ListMetadataForRunAsync(Guid loopRunId, CancellationToken cancellationToken = default)
            => inner.ListMetadataForRunAsync(loopRunId, cancellationToken);
        public Task<SessionSnapshotMetadata?> GetMetadataAsync(SessionSnapshotKey key, CancellationToken cancellationToken = default)
            => inner.GetMetadataAsync(key, cancellationToken);
        public Task ReplaceAsync(SessionSnapshotKey key, string format, Stream fullRaw, CancellationToken cancellationToken = default)
            => inner.ReplaceAsync(key, format, fullRaw, cancellationToken);
        public Task<Stream?> OpenReadAsync(SessionSnapshotKey key, long fromRawOffset = 0, CancellationToken cancellationToken = default)
            => inner.OpenReadAsync(key, fromRawOffset, cancellationToken);
        public Task<SessionSnapshotPage> ReadTailAsync(SessionSnapshotKey key, int maxEvents, long? beforeRawOffset, CancellationToken cancellationToken = default)
            => inner.ReadTailAsync(key, maxEvents, beforeRawOffset, cancellationToken);
        public Task<SessionSnapshotPage> ReadRangeAsync(SessionSnapshotKey key, long fromRawOffset, int maxEvents, CancellationToken cancellationToken = default)
            => inner.ReadRangeAsync(key, fromRawOffset, maxEvents, cancellationToken);
        public Task<bool> ForkAsync(SessionSnapshotKey source, SessionSnapshotKey destination, CancellationToken cancellationToken = default)
            => inner.ForkAsync(source, destination, cancellationToken);
    }

    [Fact]
    public async Task A_save_the_database_refuses_never_fails_the_turn()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":1}\n");
        await RunAsync();
        File.AppendAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":2}\n");
        _sql.FailWhen = command => WritesSnapshots(command.CommandText);

        await RunAsync();

        _sql.FailWhen = null;
        Assert.Equal("{\"session_id\":\"claude-sess\",\"turn\":1}\n", Encoding.UTF8.GetString((await StoredAsync())!));
    }

    [Fact]
    public async Task A_restore_that_fails_midway_leaves_no_session_file()
    {
        await SeedSnapshotAsync(SyntheticTranscript.Lines(SessionId, 9 * 1024 * 1024, seed: 23));
        Assert.True((await ChunksAsync()).Count >= 2);
        var dataReads = 0;
        _sql.FailWhen = command => SnapshotSqlRecorder.ReadsChunkData(command.CommandText) && ++dataReads == 2;

        await RunAsync(requireSuccess: false);

        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task Content_the_migration_could_not_read_as_a_claude_session_is_kept_but_never_restored()
    {
        const string unreadable = "{\"not\":\"a claude wrapper\"}";
        await using (var db = CreateDbContext())
            await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "ClaudeCode", SessionId, unreadable, TestContext.Current.CancellationToken);

        await RunAsync();
        await RunMigrationAsync();

        Assert.False(File.Exists(SessionPath));
        Assert.Equal(Encoding.UTF8.GetBytes(unreadable), await StoredAsync());
        Assert.Equal("raw", (await MetadataAsync())!.Format);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_during_the_deploy_window_ends_with_the_row_converted_to_the_current_file(bool migrationFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        Guid id;
        await using (var db = CreateDbContext())
            id = await SessionSnapshotTestSupport.SeedLegacyAsync(db, _runId, null, "ClaudeCode", SessionId,
                SessionSnapshotTestSupport.LegacyClaudeWrapper(SessionId, "{\"session_id\":\"claude-sess\",\"turn\":1}"), ct);
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"turn\":1}\n{\"session_id\":\"claude-sess\",\"turn\":2}\n");

        if (migrationFirst) await RunMigrationAsync();
        await RunAsync();

        Assert.Equal(File.ReadAllBytes(SessionPath), await StoredAsync());
        await using (var db = CreateDbContext())
            Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(db, id, ct));

        var chunks = await ChunksAsync();
        _sql.Clear();
        await RunMigrationAsync();

        Assert.DoesNotContain(_sql.Commands, c => WritesSnapshots(c.Sql));
        Assert.DoesNotContain(_migrationLog.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.All(chunks.Zip(await ChunksAsync()), pair => Assert.Equal(pair.First.Data, pair.Second.Data));
        Assert.Equal(File.ReadAllBytes(SessionPath), await StoredAsync());
    }

    private async Task RunAsync(bool requireSuccess = true, IServiceProvider? services = null)
    {
        var adapter = new ClaudeCodeAdapter((services ?? _services).GetRequiredService<IServiceScopeFactory>(), environment: _environment);
        var result = await adapter.ExecuteAsync(new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "claude-test",
                Type = "claude-code",
                BaseUrl = string.Empty,
                Model = string.Empty,
                Config = "{\"binaryPath\":\"/bin/true\"}",
            },
            Prompt: "test prompt",
            RunContext: new LoopRunContext(_runId, "wi", "t", "d", _worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            SessionId: SessionId,
            ManageSession: true));

        if (requireSuccess) Assert.True(result.Success, result.Error);
    }

    private async Task RunMigrationAsync()
    {
        using var service = new SessionSnapshotMigrationService(_services.GetRequiredService<IServiceScopeFactory>(), _migrationLog);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!;
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private SessionSnapshotKey Key => SessionSnapshotKey.ForRun(_runId, "ClaudeCode", SessionId);

    private async Task SeedSnapshotAsync(string jsonl)
    {
        await using var scope = _services.CreateAsyncScope();
        await SessionSnapshotTestSupport.SeedAsync(
            scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>(), Key, "claude-jsonl", jsonl, TestContext.Current.CancellationToken);
    }

    private async Task<SessionSnapshotMetadata?> MetadataAsync()
    {
        await using var db = CreateDbContext();
        return await new AdapterSessionSnapshotStore(db).GetMetadataAsync(Key, TestContext.Current.CancellationToken);
    }

    private async Task<byte[]?> StoredAsync()
    {
        await using var db = CreateDbContext();
        return await SessionSnapshotTestSupport.ReadAllAsync(new AdapterSessionSnapshotStore(db), Key, TestContext.Current.CancellationToken);
    }

    private async Task<List<AdapterSessionSnapshotChunk>> ChunksAsync()
    {
        await using var db = CreateDbContext();
        return await db.AdapterSessionSnapshotChunks.AsNoTracking()
            .Where(c => db.AdapterSessionSnapshots.Any(s => s.Id == c.SnapshotId && s.LoopRunId == _runId && s.SessionId == SessionId))
            .OrderBy(c => c.Seq)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static bool WritesSnapshots(string sql)
    {
        var trimmed = sql.TrimStart();
        return sql.Contains("AdapterSessionSnapshot", StringComparison.Ordinal)
            && (trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase));
    }

    private AppDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
}
