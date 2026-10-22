using ILD.Data.Entities;

namespace ILD.Data.Stores;

/// <summary>
/// The AppSettings this process has read, missing ones included, kept until a save
/// through <see cref="AppSettingStore.UpsertAsync"/> drops the key. One per process:
/// a setting edited in the database behind ILD is not seen until a restart.
/// </summary>
/// <remarks>
/// A save drops the key rather than storing its own value, so the next read loads
/// whatever the database committed last. Each key carries a version bumped by every
/// drop; a read stores what it loaded only if no save dropped the key since the read
/// began, so a load that saw the value a save was replacing is never kept.
/// </remarks>
public sealed class AppSettingCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AppSetting?> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

    internal bool TryGet(string key, out AppSetting? setting, out long version)
    {
        lock (_gate)
        {
            version = _versions.GetValueOrDefault(key);
            if (_settings.TryGetValue(key, out var cached))
            {
                setting = Copy(cached);
                return true;
            }
            setting = null;
            return false;
        }
    }

    internal void Store(string key, AppSetting? setting, long loadedAtVersion)
    {
        lock (_gate)
        {
            if (_versions.GetValueOrDefault(key) == loadedAtVersion)
                _settings[key] = Copy(setting);
        }
    }

    internal void Invalidate(string key)
    {
        lock (_gate)
        {
            _versions[key] = _versions.GetValueOrDefault(key) + 1;
            _settings.Remove(key);
        }
    }

    // Readers get their own instance, so a caller changing the entity it was handed
    // cannot change what the next reader sees.
    private static AppSetting? Copy(AppSetting? setting) => setting is null ? null : new AppSetting
    {
        Id = setting.Id,
        Key = setting.Key,
        Value = setting.Value,
        UpdatedAt = setting.UpdatedAt,
    };
}
