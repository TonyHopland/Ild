using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class NetworkModeMonitorMigratorTests
{
    private static async Task<string?> Mode(TestDb db)
        => (await db.Fresh().AppSettings.SingleOrDefaultAsync(s => s.Key == AppSettingKeys.NetworkMode, TestContext.Current.CancellationToken))?.Value;

    private static async Task<bool> RecordedAsRun(TestDb db)
        => await db.Fresh().AppSettings.AnyAsync(s => s.Key == NetworkModeMonitorMigrator.MarkerKey, TestContext.Current.CancellationToken);

    private static async Task Store(TestDb db, string value)
    {
        var fresh = db.Fresh();
        var row = await fresh.AppSettings.SingleOrDefaultAsync(s => s.Key == AppSettingKeys.NetworkMode, TestContext.Current.CancellationToken);
        if (row is null) fresh.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = AppSettingKeys.NetworkMode, Value = value });
        else row.Value = value;
        await fresh.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<int> Migrate(TestDb db)
        => NetworkModeMonitorMigrator.MigrateAsync(db.Fresh(), TestContext.Current.CancellationToken);

    [Fact]
    public void The_migrator_rewrites_the_key_the_settings_API_stores_the_mode_under()
    {
        Assert.Equal(AppSettingKeys.NetworkMode, NetworkModeMonitorMigrator.SettingKey);
        Assert.True(NetworkModeMonitorMigrator.MarkerKey.Length <= 64);
        Assert.NotEqual(NetworkModeMonitorMigrator.SettingKey, NetworkModeMonitorMigrator.MarkerKey);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData(" Off ")]
    public async Task A_stored_off_from_before_the_upgrade_becomes_monitor_and_the_run_is_recorded(string stored)
    {
        using var db = new TestDb();
        await Store(db, stored);

        Assert.Equal(1, await Migrate(db));

        Assert.Equal("monitor", await Mode(db));
        Assert.True(await RecordedAsRun(db));
    }

    [Fact]
    public async Task A_second_startup_changes_nothing_and_an_off_chosen_after_the_migration_stays_off()
    {
        using var db = new TestDb();
        await Store(db, "off");
        Assert.Equal(1, await Migrate(db));

        await Store(db, "off");

        Assert.Equal(0, await Migrate(db));
        Assert.Equal(0, await Migrate(db));
        Assert.Equal("off", await Mode(db));
    }

    [Theory]
    [InlineData("whitelist")]
    [InlineData("blacklist")]
    [InlineData("monitor")]
    [InlineData("allow")]
    public async Task Any_other_stored_value_is_left_untouched_though_the_run_is_still_recorded(string stored)
    {
        using var db = new TestDb();
        await Store(db, stored);

        Assert.Equal(0, await Migrate(db));

        Assert.Equal(stored, await Mode(db));
        Assert.True(await RecordedAsRun(db));
    }

    [Fact]
    public async Task A_fresh_database_gets_no_mode_row_and_a_later_off_is_never_rewritten()
    {
        using var db = new TestDb();

        Assert.Equal(0, await Migrate(db));
        Assert.Null(await Mode(db));
        Assert.True(await RecordedAsRun(db));

        await Store(db, "off");
        Assert.Equal(0, await Migrate(db));
        Assert.Equal("off", await Mode(db));
    }

    [Fact]
    public async Task A_backup_taken_before_the_upgrade_is_migrated_when_restored_over_a_database_that_already_ran_it()
    {
        // A restore replaces the settings table wholesale, marker included: the old
        // backup has no marker, so the next startup migrates its off.
        using var db = new TestDb();
        await Migrate(db);
        await Store(db, "off");

        var restored = db.Fresh();
        restored.AppSettings.RemoveRange(restored.AppSettings.Where(s => s.Key == NetworkModeMonitorMigrator.MarkerKey));
        await restored.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await Migrate(db));
        Assert.Equal("monitor", await Mode(db));
    }
}
