using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class NetworkPolicyStore : INetworkPolicyStore
{
    private readonly AppDbContext _db;

    public NetworkPolicyStore(AppDbContext db) { _db = db; }

    public async Task<IReadOnlyList<NetworkPolicyEntry>> GetEntriesAsync(CancellationToken ct = default)
        => await _db.NetworkPolicyEntries.AsNoTracking()
            .OrderBy(e => e.ListKind).ThenBy(e => e.Host).ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);

    public Task<NetworkPolicyEntry?> GetEntryAsync(Guid id, CancellationToken ct = default)
        => _db.NetworkPolicyEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<NetworkPolicyEntry?> FindEntryAsync(NetworkListKind kind, string host, Guid? aiProviderId, CancellationToken ct = default, bool installers = false)
        => _db.NetworkPolicyEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.ListKind == kind && e.Host == host && e.AiProviderId == aiProviderId && e.Installers == installers, ct);

    public async Task AddEntriesAsync(IReadOnlyList<NetworkPolicyEntry> entries, CancellationToken ct = default)
    {
        _db.NetworkPolicyEntries.AddRange(entries);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddEntryAsync(NetworkPolicyEntry entry, CancellationToken ct = default)
    {
        if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
        if (entry.CreatedAt == default) entry.CreatedAt = DateTime.UtcNow;
        _db.NetworkPolicyEntries.Add(entry);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<NetworkPolicyEntry?> UpdateEntryAsync(Guid id, string host, NetworkListKind kind, Guid? aiProviderId, CancellationToken ct = default, bool installers = false)
    {
        var entry = await _db.NetworkPolicyEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null) return null;
        entry.Host = host;
        entry.ListKind = kind;
        entry.AiProviderId = aiProviderId;
        entry.Installers = installers;
        await _db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<bool> DeleteEntryAsync(Guid id, CancellationToken ct = default)
        => await _db.NetworkPolicyEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<NetworkLogEntry>> GetLogAsync(int take, CancellationToken ct = default)
        => await _db.NetworkLogEntries.AsNoTracking()
            .OrderByDescending(l => l.Timestamp)
            .Take(take)
            .ToListAsync(ct);

    public Task<NetworkLogEntry?> GetLogEntryAsync(Guid id, CancellationToken ct = default)
        => _db.NetworkLogEntries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<IReadOnlyList<NetworkLogDestination>> GetLogDestinationsAsync(Guid? aiProviderId, bool installers, DateTime since, CancellationToken ct = default)
    {
        var rows = _db.NetworkLogEntries.AsNoTracking().Where(l => l.Timestamp >= since && l.Reason == null);
        rows = installers
            ? rows.Where(l => l.Installer != null)
            : rows.Where(l => l.Installer == null && (aiProviderId == null || l.AiProviderId == aiProviderId));
        var grouped = await rows
            .GroupBy(l => new { l.Host, l.AiProviderId })
            .Select(g => new { g.Key.Host, g.Key.AiProviderId, Count = g.Count(), LastSeen = g.Max(l => l.Timestamp) })
            .ToListAsync(ct);
        return [.. grouped.Select(d => new NetworkLogDestination(d.Host, d.AiProviderId, d.Count, d.LastSeen))];
    }

    public async Task AppendLogAsync(IReadOnlyList<NetworkLogEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
        {
            if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
        }
        _db.NetworkLogEntries.AddRange(entries);
        await _db.SaveChangesAsync(ct);
    }

    public Task<int> ClearLogAsync(CancellationToken ct = default)
        => _db.NetworkLogEntries.ExecuteDeleteAsync(ct);

    public Task<int> DeleteLogOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
        => _db.NetworkLogEntries.Where(l => l.Timestamp < cutoff).ExecuteDeleteAsync(ct);
}
