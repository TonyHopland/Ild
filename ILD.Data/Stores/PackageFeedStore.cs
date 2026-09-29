using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class PackageFeedStore : IPackageFeedStore
{
    private readonly AppDbContext _db;

    public PackageFeedStore(AppDbContext db) { _db = db; }

    public async Task<IReadOnlyList<PackageFeed>> GetFeedsAsync(CancellationToken ct = default)
        => (await _db.PackageFeeds.AsNoTracking().ToListAsync(ct))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public Task<PackageFeed?> GetFeedAsync(Guid id, CancellationToken ct = default)
        => _db.PackageFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task AddFeedAsync(PackageFeed feed, CancellationToken ct = default)
    {
        _db.PackageFeeds.Add(feed);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateFeedAsync(PackageFeed feed, CancellationToken ct = default)
    {
        _db.PackageFeeds.Update(feed);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteFeedAsync(Guid id, CancellationToken ct = default)
        => await _db.PackageFeeds.Where(f => f.Id == id).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<PackageFeedSelection>> GetSelectionAsync(Guid repositoryId, CancellationToken ct = default)
    {
        var selected = await _db.RepositoryPackageFeeds.AsNoTracking()
            .Where(s => s.RepositoryId == repositoryId)
            .ToListAsync(ct);
        if (selected.Count == 0)
            return [];

        var names = selected.Select(s => s.NormalizedName).ToList();
        var feeds = await _db.PackageFeeds.AsNoTracking()
            .Where(f => names.Contains(f.NormalizedName))
            .ToDictionaryAsync(f => f.NormalizedName, StringComparer.Ordinal, ct);
        return selected
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new PackageFeedSelection(s.Name, feeds.GetValueOrDefault(s.NormalizedName)))
            .ToList();
    }
}
