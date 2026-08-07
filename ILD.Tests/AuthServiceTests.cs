using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

[Collection("AuthEnvironment")]
public class AuthServiceTests
{
    private static AuthService Make(TestDb db, string password = "secret")
    {
        Environment.SetEnvironmentVariable("ILD_PASSWORD", password);
        return new AuthService(db.Auth, db.Settings);
    }

    private static async Task<string> LoginAsync(AuthService svc, string? userAgent = null)
        => (await svc.LoginAsync("admin", "secret", userAgent)).SessionToken!;

    [Fact]
    public async Task Login_with_correct_password_returns_session_token()
    {
        using var db = new TestDb();
        var svc = Make(db);

        var result = await svc.LoginAsync("admin", "secret");

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.SessionToken));
        Assert.Equal("admin", result.Username);
    }

    [Fact]
    public async Task Login_with_wrong_password_fails()
    {
        using var db = new TestDb();
        var svc = Make(db);

        var result = await svc.LoginAsync("admin", "nope");

        Assert.False(result.Success);
        Assert.Null(result.SessionToken);
    }

    [Fact]
    public async Task ValidateSession_returns_true_after_login()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        Assert.True(await svc.ValidateSessionAsync(token));
    }

    [Fact]
    public async Task Login_stores_only_the_hash_of_the_token()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        var stored = await StoredSessionAsync(db);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(UserSession.HashToken(token), stored.TokenHash);
    }

    [Fact]
    public async Task A_second_login_does_not_invalidate_the_first()
    {
        using var db = new TestDb();
        var svc = Make(db);

        var phone = await LoginAsync(svc);
        var desktop = await LoginAsync(svc);

        Assert.NotEqual(phone, desktop);
        Assert.True(await svc.ValidateSessionAsync(phone));
        Assert.True(await svc.ValidateSessionAsync(desktop));
    }

    [Fact]
    public async Task Logout_revokes_only_the_calling_session()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var phone = await LoginAsync(svc);
        var desktop = await LoginAsync(svc);

        await svc.LogoutAsync(phone);

        Assert.False(await svc.ValidateSessionAsync(phone));
        Assert.True(await svc.ValidateSessionAsync(desktop));
    }

    [Fact]
    public async Task Logout_revokes_rather_than_deletes_the_session()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        await svc.LogoutAsync(token);

        var stored = await StoredSessionAsync(db);
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task An_expired_session_is_rejected()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        await ShiftSessionBackAsync(db, TimeSpan.FromDays(200));

        Assert.False(await svc.ValidateSessionAsync(token));
        Assert.Null(await svc.GetUsernameAsync(token));
    }

    [Fact]
    public async Task An_idle_session_is_rejected_once_the_idle_window_passes()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        // Still inside the absolute cap, but untouched for longer than the idle
        // window — idle expiry alone must kill it.
        await ShiftSessionBackAsync(db, TimeSpan.FromDays(45), keepExpiryInFuture: true);

        Assert.False(await svc.ValidateSessionAsync(token));
    }

    [Fact]
    public async Task Idle_expiry_honours_the_configured_setting()
    {
        using var db = new TestDb();
        await db.Settings.UpsertAsync(AppSettingKeys.SessionIdleDays, "90");
        var svc = Make(db);
        var token = await LoginAsync(svc);

        await ShiftSessionBackAsync(db, TimeSpan.FromDays(45), keepExpiryInFuture: true);

        Assert.True(await svc.ValidateSessionAsync(token));
    }

    [Fact]
    public async Task A_zero_max_days_setting_creates_a_session_with_no_absolute_expiry()
    {
        using var db = new TestDb();
        await db.Settings.UpsertAsync(AppSettingKeys.SessionMaxDays, "0");
        var svc = Make(db);

        var result = await svc.LoginAsync("admin", "secret");

        Assert.Null(result.ExpiresAt);
        Assert.Null((await StoredSessionAsync(db)).ExpiresAt);
    }

    [Fact]
    public async Task Login_reports_the_absolute_expiry_it_stamped()
    {
        using var db = new TestDb();
        var svc = Make(db);

        var result = await svc.LoginAsync("admin", "secret");

        Assert.NotNull(result.ExpiresAt);
        Assert.Equal(
            AppSettingKeys.DefaultSessionMaxDays,
            (int)Math.Round((result.ExpiresAt!.Value - DateTime.UtcNow).TotalDays));
    }

    [Fact]
    public async Task ValidateSession_bumps_LastSeenAt_only_once_a_minute()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        // Fresh: the bump is skipped, so the stale value survives a validate.
        var stale = DateTime.UtcNow.AddSeconds(-5);
        await SetLastSeenAsync(db, stale);
        Assert.True(await svc.ValidateSessionAsync(token));
        Assert.True(Math.Abs(((await StoredSessionAsync(db)).LastSeenAt - stale).TotalSeconds) < 1);

        // Older than a minute: the bump lands.
        await SetLastSeenAsync(db, DateTime.UtcNow.AddMinutes(-5));
        Assert.True(await svc.ValidateSessionAsync(token));
        Assert.True((await StoredSessionAsync(db)).LastSeenAt > DateTime.UtcNow.AddSeconds(-30));
    }

    [Fact]
    public async Task GetSessions_lists_every_device_and_marks_the_caller()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var phone = await LoginAsync(svc, "Phone");
        await LoginAsync(svc, "Desktop");

        var sessions = await svc.GetSessionsAsync(phone);

        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.IsCurrent);
        Assert.Equal("Phone", sessions.Single(s => s.IsCurrent).UserAgent);
        Assert.Contains(sessions, s => s.UserAgent == "Desktop");
    }

    [Fact]
    public async Task GetSessions_hides_a_revoked_session()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var phone = await LoginAsync(svc, "Phone");
        var desktop = await LoginAsync(svc, "Desktop");

        await svc.LogoutAsync(desktop);

        Assert.Single(await svc.GetSessionsAsync(phone));
    }

    [Fact]
    public async Task RevokeSession_signs_out_the_named_device_only()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var phone = await LoginAsync(svc, "Phone");
        var desktop = await LoginAsync(svc, "Desktop");

        var desktopId = (await svc.GetSessionsAsync(phone)).Single(s => s.UserAgent == "Desktop").Id;
        Assert.True(await svc.RevokeSessionAsync(phone, desktopId));

        Assert.False(await svc.ValidateSessionAsync(desktop));
        Assert.True(await svc.ValidateSessionAsync(phone));
    }

    [Fact]
    public async Task RevokeSession_with_an_unknown_id_reports_failure()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        Assert.False(await svc.RevokeSessionAsync(token, Guid.NewGuid()));
    }

    [Fact]
    public async Task RevokeOtherSessions_keeps_the_calling_session_alive()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var phone = await LoginAsync(svc);
        var desktop = await LoginAsync(svc);
        var tablet = await LoginAsync(svc);

        Assert.Equal(2, await svc.RevokeOtherSessionsAsync(phone));

        Assert.True(await svc.ValidateSessionAsync(phone));
        Assert.False(await svc.ValidateSessionAsync(desktop));
        Assert.False(await svc.ValidateSessionAsync(tablet));
    }

    [Fact]
    public async Task A_revoked_token_can_do_nothing()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);
        await svc.LogoutAsync(token);

        Assert.False(await svc.ValidateSessionAsync(token));
        Assert.Null(await svc.GetUsernameAsync(token));
        Assert.Empty(await svc.GetSessionsAsync(token));
        Assert.False(await svc.RevokeSessionAsync(token, Guid.NewGuid()));
        Assert.Equal(0, await svc.RevokeOtherSessionsAsync(token));
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        using var db = new TestDb();
        var svc = Make(db);
        await LoginAsync(svc);

        Assert.False(await svc.ValidateSessionAsync("not-a-token"));
        Assert.False(await svc.ValidateSessionAsync(string.Empty));
    }

    [Fact]
    public async Task GetUsername_resolves_the_owner_of_a_live_session()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = await LoginAsync(svc);

        Assert.Equal("admin", await svc.GetUsernameAsync(token));
    }

    [Fact]
    public async Task ILD_USERNAME_overrides_the_bootstrapped_username()
    {
        using var db = new TestDb();
        Environment.SetEnvironmentVariable("ILD_PASSWORD", "secret");
        Environment.SetEnvironmentVariable("ILD_USERNAME", "tony");
        try
        {
            var svc = new AuthService(db.Auth, db.Settings);

            // The configured username bootstraps and authenticates.
            var ok = await svc.LoginAsync("tony", "secret");
            Assert.True(ok.Success);
            Assert.Equal("tony", ok.Username);

            // "admin" no longer bootstraps when a custom username is configured.
            var admin = await svc.LoginAsync("admin", "secret");
            Assert.False(admin.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ILD_USERNAME", null);
        }
    }

    /// <summary>
    /// Ages the single session in the database by <paramref name="age"/> —
    /// the only way to reach expiry without waiting for it.
    /// </summary>
    private static async Task ShiftSessionBackAsync(TestDb db, TimeSpan age, bool keepExpiryInFuture = false)
    {
        // Through the shared tracked context, not Fresh(): the store hands the
        // service the tracked instance, which would otherwise keep the old values.
        var session = await db.Context.UserSessions.SingleAsync();
        session.CreatedAt -= age;
        session.LastSeenAt -= age;
        if (!keepExpiryInFuture && session.ExpiresAt != null)
            session.ExpiresAt -= age;
        await db.Context.SaveChangesAsync();
    }

    private static async Task SetLastSeenAsync(TestDb db, DateTime lastSeenAt)
    {
        var session = await db.Context.UserSessions.SingleAsync();
        session.LastSeenAt = lastSeenAt;
        await db.Context.SaveChangesAsync();
    }

    private static async Task<UserSession> StoredSessionAsync(TestDb db)
    {
        await using var ctx = db.Fresh();
        return await ctx.UserSessions.SingleAsync();
    }
}
