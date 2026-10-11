using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

/// <summary>
/// The run row lock that serializes writes to one run: event appends, answers
/// and external signals each take it before reading what they decide on.
/// </summary>
internal static class RunRowLock
{
    /// <summary>
    /// Lock the run's row for the rest of the open transaction. False when
    /// there is no such run.
    /// </summary>
    public static async Task<bool> TakeAsync(AppDbContext db, Guid runId)
        => await db.LoopRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, r => r.Status)) > 0;
}
