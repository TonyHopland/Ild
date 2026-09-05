using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class SchedulerSettingsService : ISchedulerSettingsService
{
    private readonly IAppSettingStore _store;

    public SchedulerSettingsService(IAppSettingStore store) { _store = store; }

    public async Task<int> GetMaxConcurrentAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.SchedulerMaxConcurrent, ct);
        if (s != null && int.TryParse(s.Value, out var v) && v > 0) return v;
        return AppSettingKeys.DefaultMaxConcurrent;
    }

    public async Task<bool> GetIsPausedAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.SchedulerIsPaused, ct);
        if (s != null && bool.TryParse(s.Value, out var v)) return v;
        return AppSettingKeys.DefaultIsPaused;
    }

    public async Task<int> GetRunRetentionDaysAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.RunRetentionDays, ct);
        if (s != null && int.TryParse(s.Value, out var v) && v >= 0) return v;
        return AppSettingKeys.DefaultRunRetentionDays;
    }

    public async Task<int> GetPrHeartbeatSecondsAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.PrHeartbeatSeconds, ct);
        if (s != null && int.TryParse(s.Value, out var v) && v > 0) return v;
        return AppSettingKeys.DefaultPrHeartbeatSeconds;
    }

    public async Task<bool> GetThrottleAutoResumeAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.ThrottleAutoResume, ct);
        if (s != null && bool.TryParse(s.Value, out var v)) return v;
        return AppSettingKeys.DefaultThrottleAutoResume;
    }

    public async Task<int> GetThrottleRetryDelayMinutesAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.ThrottleRetryDelayMinutes, ct);
        if (s != null && int.TryParse(s.Value, out var v) && v > 0) return v;
        return AppSettingKeys.DefaultThrottleRetryDelayMinutes;
    }

    public async Task<int> GetThrottleMaxRetriesAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.ThrottleMaxRetries, ct);
        if (s != null && int.TryParse(s.Value, out var v) && v > 0) return v;
        return AppSettingKeys.DefaultThrottleMaxRetries;
    }

    public async Task<int> GetNetworkLogRetentionDaysAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.NetworkLogRetentionDays, ct);
        // A value past the ceiling only reaches the row by bypassing the API,
        // and is held to the longest legal window rather than the default: a
        // window nobody can have meant is no reason to start deleting sooner.
        if (s != null && int.TryParse(s.Value, out var v) && v >= 0)
            return Math.Min(v, AppSettingKeys.MaxNetworkLogRetentionDays);
        return AppSettingKeys.DefaultNetworkLogRetentionDays;
    }

    public async Task<int> GetMaxAiTraversalsAsync(CancellationToken ct = default)
    {
        var s = await _store.GetByKeyAsync(AppSettingKeys.MaxAiTraversals, ct);
        if (s != null && int.TryParse(s.Value, out var v) && v > 0) return v;
        return AppSettingKeys.DefaultMaxAiTraversals;
    }
}
