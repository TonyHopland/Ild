using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A durable rollup of one deleted chat's <see cref="ChatUsage"/> for one day and
/// (schedule, AI-provider) pair, written in the same commit that deletes the chat
/// and its usage rows, so what it cost survives the chat — the chat counterpart of
/// <see cref="LoopRunAnalyticsBucket"/>. A usage record is counted live until its
/// chat is deleted, then here, never both. Rows are only ever added, never
/// updated, so two chats archived at once cannot overwrite each other's figures;
/// analytics add them up. All metric columns are additive.
/// </summary>
public class ChatAnalyticsBucket
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The usage's day, truncated to UTC midnight.</summary>
    public DateTime BucketDate { get; set; }

    /// <summary>The schedule that had started the chat, or null for a chat no schedule started.</summary>
    public Guid? ScheduleId { get; set; }

    /// <summary>
    /// The schedule's name on the newest usage archived here, at <see cref="LastRecordedAt"/>;
    /// null for a chat no schedule started.
    /// </summary>
    [MaxLength(ChatSchedule.MaxNameLength)]
    public string? ScheduleName { get; set; }

    /// <summary>
    /// When the newest usage archived here was recorded, so the newest name a
    /// schedule had wins whatever order its chats were deleted in.
    /// </summary>
    public DateTime LastRecordedAt { get; set; }

    [MaxLength(256)]
    public string AiProvider { get; set; } = string.Empty;

    /// <summary>Turns only: title calls add to the figures but are not turns.</summary>
    public int TurnCount { get; set; }

    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
}
