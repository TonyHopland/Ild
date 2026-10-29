using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// Idempotent data migration that deletes the stored <c>network.internalAllow</c>
/// setting, which no longer exists. Runs on every startup, so a backup restored
/// from before its removal is cleaned too.
/// </summary>
public static class NetworkInternalAllowMigrator
{
    // The retired settings API key; ILD.Data cannot reference AppSettingKeys.
    public const string SettingKey = "network.internalAllow";

    /// <summary>Runs the migration; returns the number of rows removed.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        var rows = await db.AppSettings.Where(s => s.Key == SettingKey).ToListAsync(ct);
        if (rows.Count == 0) return 0;

        db.AppSettings.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
