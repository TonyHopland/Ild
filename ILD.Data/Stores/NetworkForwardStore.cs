using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class NetworkForwardStore : INetworkForwardStore
{
    private readonly AppDbContext _db;

    public NetworkForwardStore(AppDbContext db) { _db = db; }

    public async Task<IReadOnlyList<NetworkForwardEntry>> GetForwardsAsync(CancellationToken ct = default)
        => await _db.NetworkForwardEntries.AsNoTracking()
            .OrderBy(f => f.LocalPort)
            .ToListAsync(ct);

    public Task<NetworkForwardEntry?> FindByLocalPortAsync(int localPort, CancellationToken ct = default)
        => _db.NetworkForwardEntries.AsNoTracking().FirstOrDefaultAsync(f => f.LocalPort == localPort, ct);

    public async Task AddForwardAsync(NetworkForwardEntry forward, CancellationToken ct = default)
    {
        if (forward.Id == Guid.Empty) forward.Id = Guid.NewGuid();
        if (forward.CreatedAt == default) forward.CreatedAt = DateTime.UtcNow;
        _db.NetworkForwardEntries.Add(forward);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteForwardAsync(Guid id, CancellationToken ct = default)
        => await _db.NetworkForwardEntries.Where(f => f.Id == id).ExecuteDeleteAsync(ct) > 0;
}
