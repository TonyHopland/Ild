using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Analytics;

/// <summary>
/// What chat calls added up to on one UTC day for one (schedule, provider), as
/// the archive keeps it: named by the newest call, a null cost counted as none,
/// title calls in the figures but not in the turns.
/// </summary>
public sealed record ChatUsageDayTotals
{
    public required DateTime BucketDate { get; init; }
    public required Guid? ScheduleId { get; init; }
    public required string? ScheduleName { get; init; }
    public required DateTime LastRecordedAt { get; init; }
    public required string AiProvider { get; init; }
    public required int TurnCount { get; init; }
    public required long TotalInputTokens { get; init; }
    public required long TotalOutputTokens { get; init; }
    public required decimal TotalCostUsd { get; init; }
}

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
        // The rows are fixed first, so a call recorded meanwhile is neither summed
        // nor removed: it stays live, counted once.
        var ids = await db.ChatUsages
            .Where(u => u.ChatSessionId == chatSessionId)
            .Select(u => u.Id)
            .ToListAsync(ct);
        if (ids.Count == 0) return;

        foreach (var day in await SumPerDay(db.ChatUsages.Where(u => ids.Contains(u.Id))).ToListAsync(ct))
        {
            db.ChatAnalyticsBuckets.Add(new ChatAnalyticsBucket
            {
                Id = Guid.NewGuid(),
                BucketDate = day.BucketDate,
                ScheduleId = day.ScheduleId,
                ScheduleName = day.ScheduleName,
                LastRecordedAt = day.LastRecordedAt,
                AiProvider = day.AiProvider,
                TurnCount = day.TurnCount,
                TotalInputTokens = day.TotalInputTokens,
                TotalOutputTokens = day.TotalOutputTokens,
                TotalCostUsd = day.TotalCostUsd,
            });
        }

        var tracked = db.ChatUsages.Local.ToDictionary(u => u.Id);
        db.ChatUsages.RemoveRange(ids.Select(id => tracked.GetValueOrDefault(id) ?? new ChatUsage { Id = id }));
    }

    /// <summary><paramref name="usages"/> summed in the database per UTC day, schedule and provider.</summary>
    public static IQueryable<ChatUsageDayTotals> SumPerDay(IQueryable<ChatUsage> usages)
        => usages
            .GroupBy(u => new { Day = u.CreatedAt.Date, u.ScheduleId, u.AiProvider })
            .Select(g => new ChatUsageDayTotals
            {
                BucketDate = g.Key.Day,
                ScheduleId = g.Key.ScheduleId,
                ScheduleName = g.OrderByDescending(u => u.CreatedAt).Select(u => u.ScheduleName).First(),
                LastRecordedAt = g.Max(u => u.CreatedAt),
                AiProvider = g.Key.AiProvider,
                TurnCount = g.Count(u => u.Kind == ChatUsageKind.Turn),
                TotalInputTokens = g.Sum(u => u.InputTokens),
                TotalOutputTokens = g.Sum(u => u.OutputTokens),
                TotalCostUsd = g.Sum(u => u.CostUsd ?? 0m),
            });
}
