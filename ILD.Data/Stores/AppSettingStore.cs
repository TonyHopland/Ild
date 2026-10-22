using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class AppSettingStore : IAppSettingStore
{
    private readonly AppDbContext _db;
    private readonly AppSettingCache? _cache;

    public AppSettingStore(AppDbContext db, AppSettingCache? cache = null)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<AppSetting?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        if (_cache is null)
            return await LoadAsync(key, ct);

        if (_cache.TryGet(key, out var cached, out var version))
            return cached;

        var loaded = await LoadAsync(key, ct);
        _cache.Store(key, loaded, version);
        return loaded;
    }

    public async Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default)
        => await _db.AppSettings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);

    public async Task UpsertAsync(string key, string value, CancellationToken ct = default)
    {
        var existing = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing == null)
        {
            _db.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            // Also on a throw: the commit may have landed before it.
            _cache?.Invalidate(key);
        }
    }

    private Task<AppSetting?> LoadAsync(string key, CancellationToken ct)
        => _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
}
