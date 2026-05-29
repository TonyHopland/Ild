using ILD.Data.Entities;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

[Collection("AuthEnvironment")]
public class AuthServiceTests
{
    private static AuthService Make(TestDb db, string password = "secret")
    {
        Environment.SetEnvironmentVariable("ILD_PASSWORD", password);
        return new AuthService(db.Auth);
    }

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
        var token = (await svc.LoginAsync("admin", "secret")).SessionToken!;

        Assert.True((await svc.ValidateSessionAsync(token)));
    }

    [Fact]
    public async Task Logout_invalidates_session()
    {
        using var db = new TestDb();
        var svc = Make(db);
        var token = (await svc.LoginAsync("admin", "secret")).SessionToken!;

        await svc.LogoutAsync(token);

        Assert.False((await svc.ValidateSessionAsync(token)));
    }

    [Fact]
    public async Task ILD_USERNAME_overrides_the_bootstrapped_username()
    {
        using var db = new TestDb();
        Environment.SetEnvironmentVariable("ILD_PASSWORD", "secret");
        Environment.SetEnvironmentVariable("ILD_USERNAME", "tony");
        try
        {
            var svc = new AuthService(db.Auth);

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
}
