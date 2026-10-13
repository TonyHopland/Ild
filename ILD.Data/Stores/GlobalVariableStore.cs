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

    public async Task<StaledGlobalVariableProposals> AddAsync(GlobalVariable variable, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.GlobalVariables.Add(variable);
        await _db.SaveChangesAsync(ct);
        var staled = await GlobalVariableProposalStore.StalePendingAsync(_db, variable.NormalizedName, variable.UpdatedAt, ct);
        await tx.CommitAsync(ct);
        return staled;
    }

    public async Task<StaledGlobalVariableProposals> UpdateAsync(GlobalVariable variable, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.GlobalVariables.Update(variable);
        await _db.SaveChangesAsync(ct);
        var staled = await GlobalVariableProposalStore.StalePendingAsync(_db, variable.NormalizedName, variable.UpdatedAt, ct);
        await tx.CommitAsync(ct);
        return staled;
    }

    public async Task<StaledGlobalVariableProposals?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var name = await _db.GlobalVariables.Where(g => g.Id == id).Select(g => g.NormalizedName).FirstOrDefaultAsync(ct);
        if (name is null || await _db.GlobalVariables.Where(g => g.Id == id).ExecuteDeleteAsync(ct) == 0)
            return null;
        var staled = await GlobalVariableProposalStore.StalePendingAsync(_db, name, DateTime.UtcNow, ct);
        await tx.CommitAsync(ct);
        return staled;
    }
}
