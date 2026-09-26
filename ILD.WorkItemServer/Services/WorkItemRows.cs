using Microsoft.EntityFrameworkCore;

namespace ILD.WorkItemServer.Services;

/// <summary>
/// Reads and locks on a work item's row for the services that hang rows off it
/// (attachments, edit proposals) and address it by its textual id.
/// </summary>
internal static class WorkItemRows
{
    public static Task<int?> ResolveKeyAsync(WorkItemServerDbContext db, string workItemId, CancellationToken ct)
        => db.WorkItems
            .Where(w => w.Id == workItemId)
            .Select(w => (int?)w.InternalId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Take the work item's row for the rest of the transaction, so a
    /// read-then-write against its children cannot interleave with another one
    /// for the same item. Written as an update to the row's own value: it
    /// changes nothing, and it is the one lock both Postgres and SQLite take.
    /// Returns 0 when the item is gone.
    /// </summary>
    public static Task<int> ClaimAsync(WorkItemServerDbContext db, int workItemKey, CancellationToken ct)
        => db.WorkItems
            .Where(w => w.InternalId == workItemKey)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.UpdatedAt, w => w.UpdatedAt), ct);
}
