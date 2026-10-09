using ILD.Data.Enums;

namespace ILD.Data.DTOs;

/// <summary>A schedule as its owner creates or edits it.</summary>
public sealed class ChatScheduleRequest
{
    public string? Name { get; set; }
    public string? Prompt { get; set; }

    /// <summary>The provider tag; empty means the default provider.</summary>
    public string? AiTag { get; set; }

    public string? CronExpression { get; set; }

    /// <summary>An IANA zone, such as <c>Europe/Oslo</c>.</summary>
    public string? TimeZone { get; set; }

    public bool Enabled { get; set; }
    public ChatScheduleRepositoryScope RepositoryScope { get; set; }

    /// <summary>The repositories a <see cref="ChatScheduleRepositoryScope.Selected"/> schedule may use; empty otherwise.</summary>
    public List<Guid>? RepositoryIds { get; set; }

    public bool ContinueSession { get; set; }
}

/// <summary>
/// A schedule on its owner's list. <paramref name="NextFireAt"/> is null while it
/// is disabled. <paramref name="LatestChatSessionId"/> is the chat its newest
/// firing used, null before its first or once that chat is deleted.
/// </summary>
public sealed record ChatScheduleView(
    Guid Id,
    string Name,
    string Prompt,
    string? AiTag,
    string CronExpression,
    string TimeZone,
    bool Enabled,
    ChatScheduleRepositoryScope RepositoryScope,
    IReadOnlyList<Guid> RepositoryIds,
    bool ContinueSession,
    DateTime? NextFireAt,
    ChatScheduleFiringView? LastFiring,
    Guid? LatestChatSessionId);

/// <summary>One firing; <paramref name="Reason"/> says why it was skipped or failed.</summary>
public sealed record ChatScheduleFiringView(
    Guid Id,
    ChatScheduleTrigger Trigger,
    DateTime FiredAt,
    ChatScheduleFiringOutcome Outcome,
    string? Reason,
    Guid? ChatSessionId);

/// <summary>The owner's schedules, and whether the global scheduler pause holds them all.</summary>
public sealed record ChatScheduleListView(bool SchedulerPaused, IReadOnlyList<ChatScheduleView> Schedules);
