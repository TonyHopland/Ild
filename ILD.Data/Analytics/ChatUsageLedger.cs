using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Analytics;

/// <summary>
/// Writes what chats cost. Every figure is counted from exactly one place: its
/// <see cref="ChatUsage"/> row, until a chat delete moves that row into a
/// <see cref="ChatAnalyticsBucket"/> in the same commit as the delete. Nothing
/// cascades from a chat to its usage and archive rows are only ever added, so a
/// call recorded while its chat is being deleted, which the move did not take,
/// stays counted as a live row instead of being deleted unarchived or lost to
/// two archives updating the same row.
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
        }
        else
        {
            var now = DateTime.UtcNow;
            db.ChatAnalyticsBuckets.Add(new ChatAnalyticsBucket
            {
                Id = Guid.NewGuid(),
                BucketDate = now.Date,
                AiProvider = provider,
                LastRecordedAt = now,
                TotalInputTokens = usage.InputTokens,
                TotalOutputTokens = usage.OutputTokens,
                TotalCostUsd = usage.CostUsd ?? 0m,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves a soon-to-be-deleted chat's usage rows into the archive, one row per
    /// (day, schedule, provider). Queued on the context and committed by the
    /// caller's <c>SaveChanges</c> alongside the deletion.
    /// </summary>
    public static async Task ArchiveAsync(AppDbContext db, Guid chatSessionId, CancellationToken ct)
    {
        // Summed in memory: SQLite cannot sum decimals in SQL.
        var usages = await db.ChatUsages
            .Where(u => u.ChatSessionId == chatSessionId)
            .ToListAsync(ct);

        foreach (var group in usages.GroupBy(u => (Day: u.CreatedAt.Date, u.ScheduleId, u.AiProvider)))
        {
            var newest = group.MaxBy(u => u.CreatedAt)!;
            db.ChatAnalyticsBuckets.Add(new ChatAnalyticsBucket
            {
                Id = Guid.NewGuid(),
                BucketDate = group.Key.Day,
                ScheduleId = group.Key.ScheduleId,
                ScheduleName = newest.ScheduleName,
                LastRecordedAt = newest.CreatedAt,
                AiProvider = group.Key.AiProvider,
                TurnCount = group.Count(u => u.Kind == ChatUsageKind.Turn),
                TotalInputTokens = group.Sum(u => u.InputTokens),
                TotalOutputTokens = group.Sum(u => u.OutputTokens),
                TotalCostUsd = group.Sum(u => u.CostUsd ?? 0m),
            });
        }
        db.ChatUsages.RemoveRange(usages);
    }
}
