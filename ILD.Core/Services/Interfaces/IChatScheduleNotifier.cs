namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Hints to an owner's open Schedules lists that one of their Chat Schedules, or
/// one of its firings, changed. Best-effort, like <see cref="IChatNotifier"/>: a
/// dropped hint never fails what it reports.
/// </summary>
public interface IChatScheduleNotifier
{
    /// <summary>The schedule was created, edited, deleted or fired, or a firing of it ended.</summary>
    Task SchedulesChangedAsync(string userId, Guid scheduleId);
}
