using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public sealed class AnalyticsCleanupService : IAnalyticsCleanupService
{
    private readonly AppDbContext _db;

    public AnalyticsCleanupService(AppDbContext db)
    {
        _db = db;
    }

    public async Task ClearAsync(int? olderThanDays, CancellationToken cancellationToken = default)
    {
        if (olderThanDays is < 1)
            throw new ArgumentOutOfRangeException(nameof(olderThanDays), olderThanDays, "Must be at least 1.");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (olderThanDays is { } days)
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-days);
            await _db.LoopRunAnalyticsBuckets.Where(b => b.BucketDate < cutoff).ExecuteDeleteAsync(cancellationToken);
            await _db.ChatAnalyticsBuckets.Where(b => b.BucketDate < cutoff).ExecuteDeleteAsync(cancellationToken);
            await _db.ChatUsages.Where(u => u.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            await _db.LoopRunAnalyticsBuckets.ExecuteDeleteAsync(cancellationToken);
            await _db.ChatAnalyticsBuckets.ExecuteDeleteAsync(cancellationToken);
            await _db.ChatUsages.ExecuteDeleteAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
