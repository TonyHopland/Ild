using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Analytics;

/// <summary>
/// Writes what chats cost: a <see cref="ChatUsage"/> per model call while the chat
/// exists, folded into <see cref="ChatAnalyticsBucket"/> when it is deleted. A call
/// is counted in exactly one of the two, so analytics never count it twice.
/// </summary>
public static class ChatUsageLedger
{
    /// <summary>
    /// Queues one call's usage on the context, stamped with the schedule that
    /// started the chat and that schedule's current name. The caller commits.
    /// </summary>
    public static async Task AddAsync(
        AppDbContext db, Guid chatSessionId, Guid? scheduleId, Guid? chatMessageId,
        ChatUsageKind kind, TokenUsage usage, string provider, CancellationToken ct)
    {
        var scheduleName = scheduleId is null
            ? null
            : await db.ChatSchedules.Where(s => s.Id == scheduleId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        db.ChatUsages.Add(new ChatUsage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatSessionId,
            ChatMessageId = chatMessageId,
            Kind = kind,
            AiProvider = provider,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CostUsd = usage.CostUsd,
            // A schedule deleted meanwhile has unlinked the chat: it is a plain chat now.
            ScheduleId = scheduleName is null ? null : scheduleId,
            ScheduleName = scheduleName,
            CreatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Records a title call's usage under its chat, or straight into the archive
    /// when the chat was deleted while the call ran: the money is spent either way.
    /// </summary>
    public static async Task RecordTitleAsync(
        AppDbContext db, Guid chatSessionId, TokenUsage usage, string provider, CancellationToken ct)
    {
        var chat = await db.ChatSessions.AsNoTracking()
            .Where(c => c.Id == chatSessionId)
            .Select(c => new { c.ScheduleId })
            .FirstOrDefaultAsync(ct);
        if (chat is not null)
        {
            await AddAsync(db, chatSessionId, chat.ScheduleId, chatMessageId: null, ChatUsageKind.Title, usage, provider, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException)
            {
                // Deleted between the read and the write; anything else is not ours to absorb.
                foreach (var entry in db.ChangeTracker.Entries<ChatUsage>().Where(e => e.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;
                if (await db.ChatSessions.AnyAsync(c => c.Id == chatSessionId, ct)) throw;
            }
        }

        var now = DateTime.UtcNow;
        await FoldAsync(db, now.Date, Guid.Empty, string.Empty, now, provider,
            turns: 0, usage.InputTokens, usage.OutputTokens, usage.CostUsd ?? 0m, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Folds a soon-to-be-deleted chat's usage into the daily archive keyed by
    /// (day, schedule, provider). Queued on the context and committed by the
    /// caller's <c>SaveChanges</c> alongside the deletion.
    /// </summary>
    public static async Task ArchiveAsync(AppDbContext db, Guid chatSessionId, CancellationToken ct)
    {
        // Summed in memory: SQLite cannot sum decimals in SQL.
        var usages = await db.ChatUsages.AsNoTracking()
            .Where(u => u.ChatSessionId == chatSessionId)
            .ToListAsync(ct);

        foreach (var group in usages.GroupBy(u => (Day: u.CreatedAt.Date, ScheduleId: u.ScheduleId ?? Guid.Empty, u.AiProvider)))
        {
            var newest = group.MaxBy(u => u.CreatedAt)!;
            await FoldAsync(db, group.Key.Day, group.Key.ScheduleId, newest.ScheduleName ?? string.Empty, newest.CreatedAt, group.Key.AiProvider,
                group.Count(u => u.Kind == ChatUsageKind.Turn),
                group.Sum(u => u.InputTokens),
                group.Sum(u => u.OutputTokens),
                group.Sum(u => u.CostUsd ?? 0m),
                ct);
        }
    }

    private static async Task FoldAsync(
        AppDbContext db, DateTime day, Guid scheduleId, string scheduleName, DateTime recordedAt, string provider,
        int turns, long inputTokens, long outputTokens, decimal costUsd, CancellationToken ct)
    {
        var bucket = await db.ChatAnalyticsBuckets.FirstOrDefaultAsync(b =>
            b.BucketDate == day && b.ScheduleId == scheduleId && b.AiProvider == provider, ct);
        if (bucket is null)
        {
            bucket = new ChatAnalyticsBucket
            {
                Id = Guid.NewGuid(),
                BucketDate = day,
                ScheduleId = scheduleId,
                AiProvider = provider,
            };
            db.ChatAnalyticsBuckets.Add(bucket);
        }

        if (recordedAt > bucket.LastRecordedAt)
        {
            bucket.ScheduleName = scheduleName;
            bucket.LastRecordedAt = recordedAt;
        }
        bucket.TurnCount += turns;
        bucket.TotalInputTokens += inputTokens;
        bucket.TotalOutputTokens += outputTokens;
        bucket.TotalCostUsd += costUsd;
    }
}
