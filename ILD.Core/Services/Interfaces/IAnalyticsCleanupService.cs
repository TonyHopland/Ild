namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Deletes the analytics-only data: the rollups of deleted runs and deleted chats,
/// and the per-call chat usage records. Runs, chats and their messages are never
/// touched, so a run that still exists keeps reporting its figures.
/// </summary>
public interface IAnalyticsCleanupService
{
    /// <summary>
    /// Removes all of it, or with <paramref name="olderThanDays"/> only what is dated
    /// before UTC midnight of that many days ago. All three kinds go together or none do.
    /// </summary>
    Task ClearAsync(int? olderThanDays, CancellationToken cancellationToken = default);
}
