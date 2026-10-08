using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

/// <summary>
/// One firing of a <see cref="ChatSchedule"/>: when, why, in which chat and how it
/// went. What the turn did, including the work items it filed, is in that chat's
/// transcript, not here.
/// </summary>
public class ChatScheduleFiring
{
    public const int MaxReasonLength = 1024;

    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid ScheduleId { get; set; }

    /// <summary>Counts the schedule's firings from 1, so the later of two that share a time is known.</summary>
    public int Sequence { get; set; }

    public ChatScheduleTrigger Trigger { get; set; }

    public DateTime FiredAt { get; set; }

    /// <summary>The chat its turn ran in or would have; null when it never had one, or that chat is deleted.</summary>
    public Guid? ChatSessionId { get; set; }

    /// <summary>
    /// The turn it started, so the turn's end can be recorded on it. Server-side
    /// only: it never reaches an agent.
    /// </summary>
    public Guid? TurnId { get; set; }

    public ChatScheduleFiringOutcome Outcome { get; set; }

    /// <summary>Why it was skipped or failed.</summary>
    [MaxLength(MaxReasonLength)]
    public string? Reason { get; set; }

    public ChatSchedule Schedule { get; set; } = null!;

    /// <summary><paramref name="reason"/> cut to fit the column.</summary>
    public static string ClipReason(string reason)
        => reason.Length <= MaxReasonLength ? reason : reason[..(MaxReasonLength - 1)] + "…";
}
