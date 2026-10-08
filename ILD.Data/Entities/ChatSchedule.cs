using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// A user's scheduled job: a cron in an IANA time zone that starts a chat turn of
/// the owner's with <see cref="Prompt"/> (ADR-0025). A scheduled job only checks;
/// what it finds goes into work items, never into code.
/// </summary>
public class ChatSchedule : IHasUpdatedAt
{
    public const int MaxNameLength = 120;

    [Key]
    public Guid Id { get; set; }

    /// <summary>The owner, who created it; the chats it starts are theirs.</summary>
    [Required]
    [MaxLength(128)]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The job's instructions, sent as each turn's message after its context block.</summary>
    [Required]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>The provider tag a new chat is started on; null means the default provider.</summary>
    [MaxLength(AiProviderTag.MaxNameLength)]
    public string? AiTag { get; set; }

    [Required]
    [MaxLength(256)]
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>The IANA zone the cron is read in.</summary>
    [Required]
    [MaxLength(128)]
    public string TimeZone { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public ChatScheduleRepositoryScope RepositoryScope { get; set; }

    /// <summary>Comma-separated repository ids; set only for <see cref="ChatScheduleRepositoryScope.Selected"/>.</summary>
    [Required]
    public string RepositoryIdsCsv { get; set; } = string.Empty;

    /// <summary>On, every firing is a new turn in one chat; off, every firing starts a new chat.</summary>
    public bool ContinueSession { get; set; }

    /// <summary>The chat the schedule used last; null before its first chat or once that chat is deleted.</summary>
    public Guid? LatestChatSessionId { get; set; }

    /// <summary>
    /// Whether a continuing firing may use <see cref="LatestChatSessionId"/>: false once
    /// the AI tag changes after that chat was started on the old tag's provider.
    /// </summary>
    public bool LatestChatContinues { get; set; }

    /// <summary>When the cron next comes due, in UTC; null while disabled.</summary>
    public DateTime? NextFireAt { get; set; }

    /// <summary>
    /// When the earliest firing that was skipped because the scheduler was paused
    /// came due. Set, the schedule fires once as soon as the scheduler is unpaused.
    /// </summary>
    public DateTime? PendingSince { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
