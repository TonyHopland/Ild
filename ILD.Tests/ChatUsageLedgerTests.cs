using System.Data.Common;
using ILD.Data.Analytics;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ILD.Tests;

/// <summary>
/// Archiving a chat's usage: one bucket per UTC day, schedule and provider, with
/// the figures of exactly the rows the caller's save then removes.
/// </summary>
public sealed class ChatUsageLedgerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Day1 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = Day1.AddDays(1);

    private sealed record Bucket(
        DateTime BucketDate, Guid? ScheduleId, string? ScheduleName, DateTime LastRecordedAt, string AiProvider,
        int TurnCount, long InputTokens, long OutputTokens, decimal CostUsd);

    private ChatUsage Usage(
        Guid chatId, ChatUsageKind kind, string provider, long input, long output, decimal? cost,
        DateTime createdAt, Guid? scheduleId = null, string? scheduleName = null)
    {
        var usage = new ChatUsage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatId,
            Kind = kind,
            AiProvider = provider,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = cost,
            ScheduleId = scheduleId,
            ScheduleName = scheduleName,
            CreatedAt = createdAt,
        };
        using var db = _db.Fresh();
        db.ChatUsages.Add(usage);
        db.SaveChanges();
        return usage;
    }

    private List<Bucket> Buckets()
    {
        using var db = _db.Fresh();
        return db.ChatAnalyticsBuckets.AsNoTracking().ToList()
            .Select(b => new Bucket(
                b.BucketDate, b.ScheduleId, b.ScheduleName, b.LastRecordedAt, b.AiProvider,
                b.TurnCount, b.TotalInputTokens, b.TotalOutputTokens, b.TotalCostUsd))
            .OrderBy(b => b.BucketDate).ThenBy(b => b.AiProvider, StringComparer.Ordinal).ThenBy(b => b.ScheduleId)
            .ToList();
    }

    private List<Guid> LiveUsageIds(Guid chatId)
    {
        using var db = _db.Fresh();
        return db.ChatUsages.Where(u => u.ChatSessionId == chatId).Select(u => u.Id).ToList();
    }

    private async Task ArchiveAndSaveAsync(Guid chatId, AppDbContext? db = null)
    {
        var context = db ?? _db.Fresh();
        try
        {
            await ChatUsageLedger.ArchiveAsync(context, chatId, Ct);
            await context.SaveChangesAsync(Ct);
        }
        finally
        {
            if (db is null) await context.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_chats_usage_becomes_one_bucket_per_day_schedule_and_provider_with_exact_sums()
    {
        var chat = Guid.NewGuid();
        var schedule = Guid.NewGuid();
        // Inserted newest first, so neither insertion order nor key order is the time order.
        Usage(chat, ChatUsageKind.Turn, "claude", 20, 2, 0.2m, Day1.AddDays(1).AddMilliseconds(-1), schedule, "Renamed");
        Usage(chat, ChatUsageKind.Turn, "claude", 10, 1, 0.1m, Day1.AddHours(9), schedule, "Original");
        Usage(chat, ChatUsageKind.Title, "claude", 4, 3, 0.000001m, Day1.AddHours(8), schedule, "Original");
        // The schedule was deleted meanwhile: same day and provider, no schedule.
        Usage(chat, ChatUsageKind.Turn, "claude", 5, 1, null, Day1.AddHours(11));
        // A title alone on another provider: a bucket with no turns.
        Usage(chat, ChatUsageKind.Title, "haiku", 3, 1, 0.000001m, Day1.AddHours(12), schedule, "Original");
        // The next day starts a bucket of its own.
        Usage(chat, ChatUsageKind.Turn, "claude", 7, 1, null, Day2, schedule, "Renamed");
        Usage(chat, ChatUsageKind.Turn, "claude", 8, 2, 0.3m, Day2.AddHours(1), schedule, "Renamed");
        // Another chat's usage on the same day, schedule and provider stays where it is.
        var other = Guid.NewGuid();
        var untouched = Usage(other, ChatUsageKind.Turn, "claude", 1000, 100, 1m, Day1.AddHours(9), schedule, "Original");

        await ArchiveAndSaveAsync(chat);

        Assert.Equal(
            new[]
            {
                new Bucket(Day1, schedule, "Renamed", Day1.AddDays(1).AddMilliseconds(-1), "claude", 2, 34, 6, 0.300001m),
                new Bucket(Day1, null, null, Day1.AddHours(11), "claude", 1, 5, 1, 0m),
                new Bucket(Day1, schedule, "Original", Day1.AddHours(12), "haiku", 0, 3, 1, 0.000001m),
                new Bucket(Day2, schedule, "Renamed", Day2.AddHours(1), "claude", 2, 15, 3, 0.3m),
            }.OrderBy(b => b.BucketDate).ThenBy(b => b.AiProvider, StringComparer.Ordinal).ThenBy(b => b.ScheduleId),
            Buckets());
        Assert.Empty(LiveUsageIds(chat));
        Assert.Equal(new[] { untouched.Id }, LiveUsageIds(other));
    }

    [Fact]
    public async Task Sums_are_exact_decimals()
    {
        var chat = Guid.NewGuid();
        Usage(chat, ChatUsageKind.Turn, "claude", 1, 1, 0.1m, Day1.AddHours(1));
        Usage(chat, ChatUsageKind.Turn, "claude", 1, 1, 0.2m, Day1.AddHours(2));

        await ArchiveAndSaveAsync(chat);

        Assert.Equal(0.3m, Assert.Single(Buckets()).CostUsd);
    }

    [Fact]
    public async Task A_chat_without_usage_archives_nothing()
    {
        var other = Guid.NewGuid();
        var kept = Usage(other, ChatUsageKind.Turn, "claude", 1, 1, 0.1m, Day1);

        await ArchiveAndSaveAsync(Guid.NewGuid());

        Assert.Empty(Buckets());
        Assert.Equal(new[] { kept.Id }, LiveUsageIds(other));
    }

    [Fact]
    public async Task Archiving_works_in_a_context_that_already_tracks_one_of_the_chats_usage_rows()
    {
        var chat = Guid.NewGuid();
        var tracked = Usage(chat, ChatUsageKind.Turn, "claude", 10, 1, 0.1m, Day1.AddHours(1));
        Usage(chat, ChatUsageKind.Turn, "claude", 20, 2, 0.2m, Day1.AddHours(2));

        await using var db = _db.Fresh();
        Assert.NotNull(await db.ChatUsages.SingleAsync(u => u.Id == tracked.Id, Ct));
        await ArchiveAndSaveAsync(chat, db);

        var bucket = Assert.Single(Buckets());
        Assert.Equal((2, 30L, 3L, 0.3m), (bucket.TurnCount, bucket.InputTokens, bucket.OutputTokens, bucket.CostUsd));
        Assert.Empty(LiveUsageIds(chat));
    }

    [Fact]
    public async Task A_call_recorded_while_the_chat_is_being_archived_is_counted_exactly_once()
    {
        var chat = Guid.NewGuid();
        Usage(chat, ChatUsageKind.Turn, "claude", 10, 1, 0.1m, Day1.AddHours(1));
        Usage(chat, ChatUsageKind.Turn, "claude", 20, 2, 0.2m, Day1.AddHours(2));
        var late = new ChatUsage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chat,
            Kind = ChatUsageKind.Turn,
            AiProvider = "claude",
            InputTokens = 400,
            OutputTokens = 40,
            CostUsd = 4m,
            CreatedAt = Day1.AddHours(3),
        };
        // The late call lands right after the archive's first read of the usage table.
        var interleave = new AfterFirstUsageRead(() =>
        {
            using var writer = _db.Fresh();
            writer.ChatUsages.Add(late);
            writer.SaveChanges();
        });
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.Context.Database.GetDbConnection())
            .AddInterceptors(interleave)
            .Options);

        await ArchiveAndSaveAsync(chat, db);

        Assert.True(interleave.Fired);
        var archived = Buckets().Sum(b => b.InputTokens);
        using var reader = _db.Fresh();
        var live = reader.ChatUsages.Where(u => u.ChatSessionId == chat).AsEnumerable().Sum(u => u.InputTokens);
        Assert.Equal(430, archived + live);
        Assert.Equal(4.3m, Buckets().Sum(b => b.CostUsd)
            + reader.ChatUsages.Where(u => u.ChatSessionId == chat).AsEnumerable().Sum(u => u.CostUsd ?? 0m));
    }

    /// <summary>Runs <paramref name="write"/> once, as the first reader over the usage table is closed.</summary>
    private sealed class AfterFirstUsageRead(Action write) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override InterceptionResult DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Fire(command);
            return base.DataReaderClosing(command, eventData, result);
        }

        public override ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            Fire(command);
            return base.DataReaderClosingAsync(command, eventData, result);
        }

        private void Fire(DbCommand command)
        {
            if (Fired || !command.CommandText.Contains("\"ChatUsages\"", StringComparison.Ordinal)) return;
            Fired = true;
            write();
        }
    }
}
