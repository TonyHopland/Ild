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
}

/// <summary>
/// Reads scheduler runtime settings from the AppSettings table, with safe defaults.
/// </summary>
public interface ISchedulerSettingsService
{
    Task<int> GetMaxConcurrentAsync(CancellationToken ct = default);
    Task<bool> GetIsPausedAsync(CancellationToken ct = default);
}
