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
}
