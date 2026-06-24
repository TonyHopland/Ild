using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// One-time, idempotent data migration that pulls offloaded event-log payloads
/// back inline into <see cref="EventLog.Data"/>.
///
/// Historically <c>EventLogService.AppendAsync</c> spilled any payload over a
/// 10&#160;KB threshold to a file on disk, nulled <see cref="EventLog.Data"/>,
/// and recorded the file path in <see cref="EventLog.PayloadPath"/>. Payloads
/// now live in the DB (PostgreSQL TOASTs large <c>text</c> values out-of-line),
/// so this fixup reads each spilled file back into <c>Data</c> and clears the
/// path. The payload directory lived on the ephemeral <c>/app</c> image layer,
/// so a file may already be gone after a redeploy — in that case the row's data
/// is unrecoverable and we simply clear the dangling path so nothing keeps
/// pointing at a dead file.
///
/// Running it repeatedly is a cheap no-op once every row is migrated.
/// </summary>
public static class EventLogPayloadInliningMigrator
{
    /// <summary>Runs the migration; returns the number of event rows rewritten.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        var spilled = await db.EventLogs
            .Where(e => e.PayloadPath != null && e.PayloadPath != "")
            .ToListAsync(ct);
        if (spilled.Count == 0) return 0;

        foreach (var entry in spilled)
        {
            if (File.Exists(entry.PayloadPath))
                entry.Data = await File.ReadAllTextAsync(entry.PayloadPath!, ct);

            entry.PayloadPath = null;
        }

        await db.SaveChangesAsync(ct);
        return spilled.Count;
    }
}
