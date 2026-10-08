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

    /// <summary>The repositories a <see cref="ChatScheduleRepositoryScope.Selected"/> schedule may use.</summary>
    public List<Guid>? RepositoryIds { get; set; }

    public bool ContinueSession { get; set; }
}

/// <summary>A schedule on its owner's list, with when it fires next and how it last went.</summary>
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
    Guid? LatestChatSessionId,
    DateTime? NextFireAt,
    ChatScheduleFiringView? LastFiring);

/// <summary>
/// One firing. <paramref name="UnresolvedItems"/> counts creates its turn asked
/// for whose outcome is not known, so they cannot be listed as items.
/// </summary>
public sealed record ChatScheduleFiringView(
    Guid Id,
    ChatScheduleTrigger Trigger,
    DateTime? ScheduledFor,
    DateTime FiredAt,
    ChatScheduleFiringOutcome Outcome,
    string? Reason,
    Guid? ChatSessionId,
    IReadOnlyList<string> CreatedWorkItemIds,
    int UnresolvedItems);

/// <summary>A create or update: the saved schedule, or why it was refused.</summary>
public sealed record ChatScheduleSaveResult(ChatScheduleView? Schedule, string? Error);
