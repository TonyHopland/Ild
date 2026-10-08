using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>One time a <see cref="ChatSchedule"/> came due or was run by hand, and how it went.</summary>
public class ChatScheduleFiring
{
    public const int MaxReasonLength = 2000;

    [Key]
    public Guid Id { get; set; }

    public Guid ChatScheduleId { get; set; }

    public ChatSchedule? ChatSchedule { get; set; }

    /// <summary>Counts the schedule's firings from 1, so the latest is known even when two share a time.</summary>
    public int Number { get; set; }

    public ChatScheduleTrigger Trigger { get; set; }

    /// <summary>When the cron came due; null for Run now.</summary>
    public DateTime? ScheduledFor { get; set; }

    public DateTime FiredAt { get; set; }

    public ChatScheduleFiringOutcome Outcome { get; set; }

    /// <summary>Why it was skipped or failed.</summary>
    [MaxLength(MaxReasonLength)]
    public string? Reason { get; set; }

    /// <summary>The chat its turn ran in; null when it got none or that chat was deleted.</summary>
    public Guid? ChatSessionId { get; set; }

    /// <summary>The turn it started; null when it started none.</summary>
    public Guid? TurnId { get; set; }

    public List<ChatScheduleFiringWorkItem> WorkItems { get; set; } = new();

    /// <summary>A reason that fits its column.</summary>
    public static string ClipReason(string reason)
        => reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength];
}
