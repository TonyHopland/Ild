using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// The tokens and cost one model call of a <see cref="ChatSession"/> reported: a
/// turn's reply or the call that titled the chat. Written only when the provider
/// reported usage, so a chat from before usage was recorded contributes nothing.
/// Moved into <see cref="ChatAnalyticsBucket"/> when its chat is deleted. Neither
/// id is a foreign key: a cascade from the chat would delete a row recorded after
/// the move unarchived, so a row the move missed outlives its chat and stays counted.
/// </summary>
public class ChatUsage
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid ChatSessionId { get; set; }

    /// <summary>The turn's assistant reply; null for a title call.</summary>
    public Guid? ChatMessageId { get; set; }

    public ChatUsageKind Kind { get; set; }

    /// <summary>The name of the provider that made the call.</summary>
    [Required]
    [MaxLength(256)]
    public string AiProvider { get; set; } = string.Empty;

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Null when the provider reported no cost.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>
    /// The schedule that had started the chat when the call was made, and its name
    /// then. Stamped rather than read from the chat, which a deleted schedule unlinks.
    /// </summary>
    public Guid? ScheduleId { get; set; }

    [MaxLength(ChatSchedule.MaxNameLength)]
    public string? ScheduleName { get; set; }

    public DateTime CreatedAt { get; set; }
}
