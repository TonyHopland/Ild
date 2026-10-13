using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class GlobalVariableStore : IGlobalVariableStore
{
    private readonly AppDbContext _db;

    public GlobalVariableStore(AppDbContext db) { _db = db; }

    public async Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct = default)
        => (await _db.GlobalVariables.AsNoTracking().ToListAsync(ct))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public Task<GlobalVariable?> GetAsync(Guid id, CancellationToken ct = default)
        => _db.GlobalVariables.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task AddAsync(GlobalVariable variable, CancellationToken ct = default)
    {
        _db.GlobalVariables.Add(variable);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(GlobalVariable variable, CancellationToken ct = default)
    {
        _db.GlobalVariables.Update(variable);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => await _db.GlobalVariables.Where(g => g.Id == id).ExecuteDeleteAsync(ct) > 0;
}
