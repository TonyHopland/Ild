using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

public interface IAppSettingStore
{
    Task<AppSetting?> GetByKeyAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(string key, string value, CancellationToken ct = default);
}
