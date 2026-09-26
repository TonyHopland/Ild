using ILD.Core.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The bootstrap admin comes from <c>ILD_USERNAME</c> and <c>ILD_PASSWORD</c>, read
/// through a reader a test supplies rather than the process environment. Driven
/// through <see cref="AuthService.LoginAsync"/>, because what the rules decide is
/// who can sign in first.
/// </summary>
public class BootstrapCredentialsTests
{
    private static AuthService Make(TestDb db, Dictionary<string, string?> variables)
        => new(db.Auth, db.Settings,
            BootstrapCredentials.FromEnvironment(name => variables.TryGetValue(name, out var value) ? value : null));

    [Theory]
    // ILD_USERNAME unset, blank or whitespace: the bootstrap user is "admin".
    [InlineData(null, "secret", "admin", "secret")]
    [InlineData("", "secret", "admin", "secret")]
    [InlineData("   ", "secret", "admin", "secret")]
    // Set: it replaces "admin", trimmed.
    [InlineData("tony", "secret", "tony", "secret")]
    [InlineData("  tony  ", "secret", "tony", "secret")]
    // The password is taken as it is, surrounding spaces included.
    [InlineData(null, " spaced ", "admin", " spaced ")]
    public async Task The_configured_bootstrap_user_signs_in_with_the_configured_password(
        string? username, string password, string expectedUsername, string expectedPassword)
    {
        using var db = new TestDb();
        var svc = Make(db, new() { ["ILD_USERNAME"] = username, ["ILD_PASSWORD"] = password });

        var result = await svc.LoginAsync(expectedUsername, expectedPassword);

        Assert.True(result.Success);
        Assert.Equal(expectedUsername, result.Username);
    }

    [Fact]
    public async Task A_configured_username_stops_admin_from_bootstrapping()
    {
        using var db = new TestDb();
        var svc = Make(db, new() { ["ILD_USERNAME"] = "tony", ["ILD_PASSWORD"] = "secret" });

        Assert.False((await svc.LoginAsync("admin", "secret")).Success);
        Assert.True((await svc.LoginAsync("tony", "secret")).Success);
    }

    [Fact]
    public async Task The_password_is_not_trimmed()
    {
        using var db = new TestDb();
        var svc = Make(db, new() { ["ILD_PASSWORD"] = " spaced " });

        Assert.False((await svc.LoginAsync("admin", "spaced")).Success);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Without_a_password_there_is_no_bootstrap_user(string? password)
    {
        using var db = new TestDb();
        var svc = Make(db, new() { ["ILD_USERNAME"] = "tony", ["ILD_PASSWORD"] = password });

        Assert.False((await svc.LoginAsync("tony", password ?? "")).Success);
        Assert.False((await svc.LoginAsync("admin", password ?? "")).Success);
        Assert.Empty(await db.Context.Users.ToListAsync(TestContext.Current.CancellationToken));
    }
}
