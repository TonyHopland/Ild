using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// Data migration for the network-mode rename: before it, a stored <c>off</c> meant
/// "allow and log everything", which is now <c>monitor</c>; <c>off</c> now means no
/// proxy and no log. Because <c>off</c> is a legitimate value again, the rewrite runs
/// once per database, recorded by <see cref="MarkerKey"/> in the same save as the
/// rewrite. A backup restored from before the upgrade carries no marker, so the
/// next startup migrates it.
/// </summary>
public static class NetworkModeMonitorMigrator
{
    // The settings API's network.mode key; ILD.Data cannot reference AppSettingKeys.
    public const string SettingKey = "network.mode";
    public const string MarkerKey = "migration.networkModeMonitor";

    /// <summary>Runs the migration; returns the number of values rewritten.</summary>
    public static async Task<int> MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.AppSettings.AnyAsync(s => s.Key == MarkerKey, ct))
            return 0;

        var migrated = 0;
        var mode = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (mode is not null && mode.Value.Trim().ToLowerInvariant() == "off")
        {
            mode.Value = "monitor";
            migrated = 1;
        }

        db.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = MarkerKey, Value = "done" });
        await db.SaveChangesAsync(ct);
        return migrated;
    }
}
