using ILD.Core.Services.Implementations;
using ILD.Data.Entities;
using ILD.Data.Migrations;
using ILD.Data.Security;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The carry-across that keeps the deploy from signing everybody out. The
/// current schema has no <c>Users.SessionToken</c>, so each test re-creates the
/// legacy column to stand in for a database that predates the sessions table.
/// </summary>
[Collection("AuthEnvironment")]
public class UserSessionCarryOverMigratorTests
{
    [Fact]
    public async Task An_existing_plaintext_token_keeps_working_after_the_carry_across()
    {
        using var db = new TestDb();
        var user = await SeedLegacyUserAsync(db, "legacy-token-from-the-old-column");

        var carried = await UserSessionCarryOverMigrator.CaptureAsync(db.Context);
        var created = await UserSessionCarryOverMigrator.ApplyAsync(
            db.Context, carried, DateTime.UtcNow.AddDays(90));

        Assert.Equal(1, created);

        Environment.SetEnvironmentVariable("ILD_PASSWORD", "secret");
        var svc = new AuthService(db.Auth, db.Settings);
        Assert.True(await svc.ValidateSessionAsync("legacy-token-from-the-old-column"));
        Assert.Equal(user.Username, await svc.GetUsernameAsync("legacy-token-from-the-old-column"));
    }

    [Fact]
    public async Task The_carried_token_is_stored_hashed_not_in_plaintext()
    {
        using var db = new TestDb();
        await SeedLegacyUserAsync(db, "legacy-token");

        var carried = await UserSessionCarryOverMigrator.CaptureAsync(db.Context);
        await UserSessionCarryOverMigrator.ApplyAsync(db.Context, carried, null);

        var session = await db.Context.UserSessions.SingleAsync();
        Assert.Equal(SessionTokenHasher.Hash("legacy-token"), session.TokenHash);
        Assert.DoesNotContain("legacy-token", session.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_the_same_capture_twice_creates_one_session()
    {
        using var db = new TestDb();
        await SeedLegacyUserAsync(db, "legacy-token");

        var carried = await UserSessionCarryOverMigrator.CaptureAsync(db.Context);
        Assert.Equal(1, await UserSessionCarryOverMigrator.ApplyAsync(db.Context, carried, null));
        Assert.Equal(0, await UserSessionCarryOverMigrator.ApplyAsync(db.Context, carried, null));
        Assert.Equal(1, await db.Context.UserSessions.CountAsync());
    }

    [Fact]
    public async Task Capture_is_empty_when_the_legacy_column_is_gone()
    {
        using var db = new TestDb();
        await db.Auth.CreateUserAsync(new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = "x",
            CreatedAt = DateTime.UtcNow,
        });

        Assert.Empty(await UserSessionCarryOverMigrator.CaptureAsync(db.Context));
    }

    [Fact]
    public async Task A_user_with_no_token_carries_nothing_across()
    {
        using var db = new TestDb();
        await SeedLegacyUserAsync(db, sessionToken: null);

        var carried = await UserSessionCarryOverMigrator.CaptureAsync(db.Context);

        Assert.Empty(carried);
        Assert.Equal(0, await UserSessionCarryOverMigrator.ApplyAsync(db.Context, carried, null));
    }

    /// <summary>
    /// Re-creates the dropped <c>Users.SessionToken</c> column and seeds a user
    /// holding <paramref name="sessionToken"/> in it, exactly as a database on
    /// the previous version would.
    /// </summary>
    private static async Task<User> SeedLegacyUserAsync(TestDb db, string? sessionToken)
    {
        await db.Context.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Users"" ADD COLUMN ""SessionToken"" TEXT");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = "x",
            CreatedAt = DateTime.UtcNow,
        };
        await db.Auth.CreateUserAsync(user);

        if (sessionToken != null)
        {
            await db.Context.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Users"" SET ""SessionToken"" = {0} WHERE ""Username"" = 'admin'",
                sessionToken);
        }

        return user;
    }
}
