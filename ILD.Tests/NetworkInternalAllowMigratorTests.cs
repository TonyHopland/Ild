using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class NetworkInternalAllowMigratorTests
{
    private static async Task Store(TestDb db, string key, string value)
    {
        var fresh = db.Fresh();
        fresh.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = key, Value = value });
        await fresh.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string?> Value(TestDb db, string key)
        => (await db.Fresh().AppSettings.SingleOrDefaultAsync(s => s.Key == key, TestContext.Current.CancellationToken))?.Value;

    private static Task<int> Migrate(TestDb db)
        => NetworkInternalAllowMigrator.MigrateAsync(db.Fresh(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_stored_row_is_removed_alone_and_a_second_startup_changes_nothing()
    {
        using var db = new TestDb();
        await Store(db, "network.internalAllow", ".lan.example\n10.0.0.0/8");
        await Store(db, AppSettingKeys.NetworkMode, "whitelist");

        Assert.Equal(1, await Migrate(db));

        Assert.Null(await Value(db, "network.internalAllow"));
        Assert.Equal("whitelist", await Value(db, AppSettingKeys.NetworkMode));
        Assert.Equal(0, await Migrate(db));
        Assert.Equal("whitelist", await Value(db, AppSettingKeys.NetworkMode));
    }

    [Fact]
    public async Task A_fresh_database_is_left_alone()
    {
        using var db = new TestDb();

        Assert.Equal(0, await Migrate(db));
    }
}
