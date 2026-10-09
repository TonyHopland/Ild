using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// A Chat Schedule (ADR-0026): starts a chat turn of its owner's with a set prompt
/// on a cron, read in <see cref="TimeZone"/>. Lives on the Ild instance only; the
/// WorkItem Server knows nothing of it.
/// </summary>
public class ChatSchedule : IHasUpdatedAt
{
    public const int MaxNameLength = 120;

    [Key]
    public Guid Id { get; set; }

    /// <summary>The owner (the authenticated username); its chats are theirs.</summary>
    [Required]
    [MaxLength(128)]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The job's instructions, sent after the firing's context block.</summary>
    [Required]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>The provider tag; null means the default provider.</summary>
    [MaxLength(AiProviderTag.MaxNameLength)]
    public string? AiTag { get; set; }

    [Required]
    [MaxLength(256)]
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>An IANA zone id, such as <c>Europe/Oslo</c>.</summary>
    [Required]
    [MaxLength(128)]
    public string TimeZone { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public ChatScheduleRepositoryScope RepositoryScope { get; set; }

    /// <summary>Comma-separated repository ids, for <see cref="ChatScheduleRepositoryScope.Selected"/> only.</summary>
    [Required]
    public string RepositoryIdsCsv { get; set; } = string.Empty;

    /// <summary>
    /// Every firing is a new turn in the schedule's chat (<see cref="ContinueChatSessionId"/>)
    /// rather than a new chat, until that chat is deleted or no longer on the provider the AI tag picks.
    /// </summary>
    public bool ContinueSession { get; set; }

    /// <summary>
    /// The chat a continued firing goes to: the schedule's newest chat, cleared when
    /// its AI tag changes, since a chat keeps its provider for life. Not a foreign
    /// key, which would make a cycle with <see cref="ChatSession.ScheduleId"/>; a
    /// firing checks that the chat still exists, is the owner's, and is on the provider the tag picks now.
    /// </summary>
    public Guid? ContinueChatSessionId { get; set; }

    /// <summary>When the cron next comes due, in UTC; null while disabled.</summary>
    public DateTime? NextFireAt { get; set; }

    /// <summary>
    /// Set when a firing came due while the scheduler was paused: the schedule owes
    /// one firing, made once the pause is lifted, however many it missed.
    /// </summary>
    public DateTime? PendingSince { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
