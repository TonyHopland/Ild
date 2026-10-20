using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ILD.Tests;

/// <summary>
/// Seeding and reading session snapshots through <see cref="IAdapterSessionSnapshotStore"/>,
/// and standing in for a row the content migration has not reached yet.
/// </summary>
internal static class SessionSnapshotTestSupport
{
    public static Task SeedAsync(IAdapterSessionSnapshotStore store, SessionSnapshotKey key, string format, string raw, CancellationToken ct)
        => SeedAsync(store, key, format, Encoding.UTF8.GetBytes(raw), ct);

    public static async Task SeedAsync(IAdapterSessionSnapshotStore store, SessionSnapshotKey key, string format, byte[] raw, CancellationToken ct)
    {
        using var stream = new MemoryStream(raw);
        await store.ReplaceAsync(key, format, stream, ct);
    }

    public static async Task<byte[]?> ReadAllAsync(IAdapterSessionSnapshotStore store, SessionSnapshotKey key, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(key, 0, ct);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }

    public static async Task<string?> ReadAllTextAsync(IAdapterSessionSnapshotStore store, SessionSnapshotKey key, CancellationToken ct)
        => await ReadAllAsync(store, key, ct) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>
    /// A row as migration A leaves it: the transcript still in the legacy
    /// <c>SessionJson</c> column and the new metadata columns at their defaults.
    /// </summary>
    public static async Task<Guid> SeedLegacyAsync(
        AppDbContext db, Guid? loopRunId, Guid? chatSessionId, string adapterName, string sessionId, string sessionJson, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AdapterSessionSnapshots"
                ("Id", "LoopRunId", "ChatSessionId", "AdapterName", "SessionId", "SessionJson",
                 "Format", "Compression", "ByteLength", "ChunkCount", "EventCount", "CreatedAt")
            VALUES ({id}, {loopRunId}, {chatSessionId}, {adapterName}, {sessionId}, {sessionJson},
                    '', '', 0, 0, 0, {DateTime.UtcNow})
            """, ct);
        return id;
    }

    public static async Task<string?> ReadLegacySessionJsonAsync(AppDbContext db, Guid snapshotId, CancellationToken ct)
    {
        var values = await db.Database
            .SqlQuery<string?>($"""SELECT "SessionJson" AS "Value" FROM "AdapterSessionSnapshots" WHERE "Id" = {snapshotId}""")
            .ToListAsync(ct);
        return Assert.Single(values);
    }

    /// <summary>The claude-jsonl wrapper the old ClaudeCodeAdapter.WrapJsonl stored.</summary>
    public static string LegacyClaudeWrapper(string sessionId, params string[] eventJson)
        => $$"""{"format":"claude-jsonl","sessionId":"{{sessionId}}","events":[{{string.Join(",", eventJson)}}]}""";
}

/// <summary>
/// Records every command a context sends, with the bytes of parameter data it carries,
/// and can make one chosen command fail.
/// </summary>
internal sealed class SnapshotSqlRecorder : DbCommandInterceptor
{
    private readonly List<(string Sql, long ParameterBytes)> _commands = [];

    public IReadOnlyList<(string Sql, long ParameterBytes)> Commands { get { lock (_commands) return _commands.ToList(); } }

    /// <summary>Throws instead of running a command this returns true for.</summary>
    public Func<DbCommand, bool>? FailWhen { get; set; }

    public void Clear() { lock (_commands) _commands.Clear(); }

    public long ParameterBytes => Commands.Sum(c => c.ParameterBytes);

    /// <summary>Queries that read chunk content (the <c>Data</c> column of the chunk table).</summary>
    public int ChunkDataReads => Commands.Count(c => ReadsChunkData(c.Sql));

    public static bool ReadsChunkData(string sql)
        => sql.Contains("AdapterSessionSnapshotChunks", StringComparison.Ordinal)
            && sql.Contains("\"Data\"", StringComparison.Ordinal)
            && sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);

    public static bool TouchesChunks(string sql) => sql.Contains("AdapterSessionSnapshotChunks", StringComparison.Ordinal);

    /// <summary>A query that reads the legacy column's content, not only whether it is null.</summary>
    public static bool SelectsSessionJson(string sql)
        => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && Regex.Replace(sql, """("\w+"\.)?"SessionJson"\s+IS\s+(NOT\s+)?NULL""", string.Empty, RegexOptions.IgnoreCase)
                .Contains("SessionJson", StringComparison.Ordinal);

    private void Record(DbCommand command)
    {
        long bytes = 0;
        foreach (DbParameter parameter in command.Parameters)
        {
            bytes += parameter.Value switch
            {
                byte[] b => b.Length,
                string s => Encoding.UTF8.GetByteCount(s),
                null or DBNull => 0,
                _ => 16,
            };
        }
        lock (_commands) _commands.Add((command.CommandText, bytes));
        if (FailWhen?.Invoke(command) == true)
            throw new InvalidOperationException("injected database failure");
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }
}

/// <summary>Transcript-shaped JSONL: Claude-like events of varied length, every one naming the session.</summary>
internal static class SyntheticTranscript
{
    private static readonly string[] Words =
    [
        "the", "build", "test", "file", "run", "agent", "change", "commit", "branch", "review",
        "error", "fixed", "value", "method", "class", "return", "string", "async", "await", "task",
        "update", "migration", "store", "chunk", "session", "line", "offset", "length", "stream", "read",
    ];

    public static string Lines(string sessionId, int approxBytes, int seed = 1)
    {
        var random = new Random(seed);
        var text = new StringBuilder();
        var turn = 0;
        while (text.Length < approxBytes)
        {
            var words = random.Next(3, 400);
            var body = new StringBuilder();
            for (var i = 0; i < words; i++)
                body.Append(Words[random.Next(Words.Length)]).Append(' ');
            var role = turn % 2 == 0 ? "user" : "assistant";
            text.Append($$"""{"type":"{{role}}","sessionId":"{{sessionId}}","uuid":"{{new Guid(random.NextBytes(16))}}","message":{"role":"{{role}}","content":[{"type":"text","text":"{{body}}"}]},"timestamp":"2026-10-20T15:{{turn % 60:00}}:00Z"}""");
            text.Append('\n');
            turn++;
        }
        return text.ToString();
    }

    private static byte[] NextBytes(this Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }
}

internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception), exception));
    }
}
