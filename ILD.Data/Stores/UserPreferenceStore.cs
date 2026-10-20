using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class UserPreferenceStore : IUserPreferenceStore
{
    private readonly AppDbContext _db;

    public UserPreferenceStore(AppDbContext db) { _db = db; }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(Guid userId, CancellationToken ct = default)
        => await _db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.Key, p => p.Value, ct);

    public async Task UpsertAsync(Guid userId, string key, string value, CancellationToken ct = default)
    {
        var existing = await _db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key, ct);
        if (existing == null)
        {
            _db.UserPreferences.Add(new UserPreference { Id = Guid.NewGuid(), UserId = userId, Key = key, Value = value });
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two devices set the same new key at once and the other insert
                // landed first: the unique index refused this one, so it becomes
                // an update of that row instead.
                _db.ChangeTracker.Clear();
                var landed = await _db.UserPreferences.FirstAsync(p => p.UserId == userId && p.Key == key, ct);
                landed.Value = value;
                await _db.SaveChangesAsync(ct);
            }
            return;
        }
        existing.Value = value;
        await _db.SaveChangesAsync(ct);
    }
}
