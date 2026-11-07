using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Api.Services;

internal static class LiveRunIds
{
    /// <summary>The runs that are still going or waiting for a person, and every chat: what a run host keeps when it clears what the others left.</summary>
    public static async Task<IReadOnlySet<Guid>> GetAsync(AppDbContext dbContext, CancellationToken ct)
        => (await dbContext.LoopRuns
                .Where(r => r.Status == LoopRunStatus.Running || r.Status == LoopRunStatus.WaitingHuman)
                .Select(r => r.Id)
                .ToListAsync(ct))
            .Concat(await dbContext.ChatSessions.Select(c => c.Id).ToListAsync(ct))
            .ToHashSet();
}
