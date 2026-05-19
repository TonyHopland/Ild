using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class AppSettingStore : IAppSettingStore
{
    private readonly AppDbContext _db;

    public AppSettingStore(AppDbContext db) { _db = db; }

    public Task<AppSetting?> GetByKeyAsync(string key, CancellationToken ct = default)
        => _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);

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
        await _db.SaveChangesAsync(ct);
    }
}
