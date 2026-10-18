using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class GlobalVariableStore : IGlobalVariableStore
{
    private readonly AppDbContext _db;
    private readonly AgentProposals<GlobalVariableProposal, GlobalVariableProposalStatus> _proposals;

    public GlobalVariableStore(AppDbContext db)
    {
        _db = db;
        _proposals = new(db);
    }

    public async Task<IReadOnlyList<GlobalVariable>> GetAllAsync(CancellationToken ct = default)
        => (await _db.GlobalVariables.AsNoTracking().ToListAsync(ct))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public Task<GlobalVariable?> GetAsync(Guid id, CancellationToken ct = default)
        => _db.GlobalVariables.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<StaledProposals> AddAsync(GlobalVariable variable, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.GlobalVariables.Add(variable);
        await _db.SaveChangesAsync(ct);
        var staled = await StalePendingAsync(variable.NormalizedName, variable.UpdatedAt, ct);
        await tx.CommitAsync(ct);
        return staled;
    }

    public async Task<StaledProposals> UpdateAsync(GlobalVariable variable, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.GlobalVariables.Update(variable);
        await _db.SaveChangesAsync(ct);
        var staled = await StalePendingAsync(variable.NormalizedName, variable.UpdatedAt, ct);
        await tx.CommitAsync(ct);
        return staled;
    }

    public async Task<StaledProposals?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var name = await _db.GlobalVariables.Where(g => g.Id == id).Select(g => g.NormalizedName).FirstOrDefaultAsync(ct);
        if (name is null || await _db.GlobalVariables.Where(g => g.Id == id).ExecuteDeleteAsync(ct) == 0)
            return null;
        var staled = await StalePendingAsync(name, DateTime.UtcNow, ct);
        await tx.CommitAsync(ct);
        return staled;
    }

    /// <summary>Every pending suggestion for the variable goes stale, in the transaction of the write to it.</summary>
    private Task<StaledProposals> StalePendingAsync(string normalizedName, DateTime now, CancellationToken ct)
        => _proposals.StalePendingAsync(p => p.NormalizedName == normalizedName, now, ct);
}
