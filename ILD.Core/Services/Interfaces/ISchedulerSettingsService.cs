namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Well-known <see cref="ILD.Data.Entities.AppSetting"/> keys.
/// </summary>
public static class AppSettingKeys
{
    public const string SchedulerMaxConcurrent = "scheduler.maxConcurrent";
    public const string SchedulerIsPaused = "scheduler.isPaused";

    public const int DefaultMaxConcurrent = 5;
    public const bool DefaultIsPaused = false;

    /// <summary>
    /// Days after a run reaches a terminal state before the worktree retention
    /// sweeper reclaims its worktree/branch and deletes the run. <c>0</c>
    /// disables automatic reclamation entirely.
    /// </summary>
    public const string RunRetentionDays = "run.retentionDays";
    public const int DefaultRunRetentionDays = 30;

    // WorkItem server connection settings (previously stored per RemoteProvider).
    public const string WorkItemServerUrl = "workItemServer.url";
    public const string WorkItemServerApiKey = "workItemServer.apiKey";
    public const string WorkItemServerPollIntervalSeconds = "workItemServer.pollIntervalSeconds";
    public const string WorkItemServerGraceIntervalSeconds = "workItemServer.graceIntervalSeconds";

    public const int DefaultPollIntervalSeconds = 60;
    public const int DefaultGraceIntervalSeconds = 5;
}

/// <summary>
/// Reads scheduler runtime settings from the AppSettings table, with safe defaults.
/// </summary>
public interface ISchedulerSettingsService
{
    Task<int> GetMaxConcurrentAsync(CancellationToken ct = default);
    Task<bool> GetIsPausedAsync(CancellationToken ct = default);

    /// <summary>
    /// Run retention window in days; <c>0</c> means never auto-reclaim.
    /// </summary>
    Task<int> GetRunRetentionDaysAsync(CancellationToken ct = default);
}
