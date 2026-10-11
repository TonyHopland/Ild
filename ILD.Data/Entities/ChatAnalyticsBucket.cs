using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A durable rollup of one day's <see cref="ChatUsage"/> for a (schedule,
/// AI-provider) pair, written when a chat is deleted so what it cost survives the
/// chat — the chat counterpart of <see cref="LoopRunAnalyticsBucket"/>. A usage
/// record is counted live until its chat is deleted, then here, never both. All
/// metric columns are additive.
/// </summary>
public class ChatAnalyticsBucket
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The usage's day, truncated to UTC midnight.</summary>
    public DateTime BucketDate { get; set; }

    /// <summary>
    /// The schedule that started the chats, or <see cref="Guid.Empty"/> for chats no
    /// schedule started: the unique index would never match a null.
    /// </summary>
    public Guid ScheduleId { get; set; }

    /// <summary>
    /// The schedule's name on the newest usage folded in, at <see cref="LastRecordedAt"/>;
    /// empty for chats no schedule started.
    /// </summary>
    [MaxLength(ChatSchedule.MaxNameLength)]
    public string ScheduleName { get; set; } = string.Empty;

    /// <summary>
    /// When the newest usage folded in was recorded. Chats are deleted in any order,
    /// so an older name must not replace a newer one.
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
