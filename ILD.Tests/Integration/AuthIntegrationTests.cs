using System.Net;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

public class AuthIntegrationTests
{
    [Fact]
    public async Task Login_with_valid_admin_password_returns_200_and_token()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "admin",
            password = factory.AdminPassword,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseBody>(CaseInsensitive, TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.Equal("admin", body.Username);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "admin",
            password = "definitely-wrong",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_valid_token_returns_200()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_reports_when_the_session_expires()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "admin",
            password = factory.AdminPassword,
        }, cancellationToken: TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<LoginResponseBody>(CaseInsensitive, TestContext.Current.CancellationToken);
        Assert.NotNull(body!.ExpiresAt);
        Assert.True(body.ExpiresAt > DateTime.UtcNow.AddDays(89));
    }

    [Fact]
    public async Task A_second_login_leaves_the_first_device_signed_in()
    {
        await using var factory = new ApiFactory();
        var phone = await factory.CreateAuthenticatedClientAsync();

        // The headline bug: this used to overwrite the one session column.
        _ = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Logout_signs_out_only_the_device_that_called_it()
    {
        await using var factory = new ApiFactory();
        var phone = await factory.CreateAuthenticatedClientAsync();
        var desktop = await factory.CreateAuthenticatedClientAsync();

        (await desktop.PostAsync("/api/v1/auth/logout", null, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Sessions_lists_every_device_without_leaking_a_token()
    {
        await using var factory = new ApiFactory();
        var phone = await factory.CreateAuthenticatedClientAsync();
        var desktopToken = await factory.GetAdminTokenAsync();

        var response = await phone.GetAsync("/api/v1/auth/sessions", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(desktopToken, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);

        var sessions = await response.Content.ReadFromJsonAsync<SessionBody[]>(CaseInsensitive, TestContext.Current.CancellationToken);
        Assert.Equal(2, sessions!.Length);
        Assert.Single(sessions, s => s.IsCurrent);
    }

    [Fact]
    public async Task Revoking_a_session_signs_that_device_out()
    {
        await using var factory = new ApiFactory();
        var phone = await factory.CreateAuthenticatedClientAsync();
        var desktop = await factory.CreateAuthenticatedClientAsync();

        var sessions = await phone.GetFromJsonAsync<SessionBody[]>("/api/v1/auth/sessions", CaseInsensitive, TestContext.Current.CancellationToken);
        var other = sessions!.Single(s => !s.IsCurrent);

        var revoke = await phone.DeleteAsync($"/api/v1/auth/sessions/{other.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Revoking_others_keeps_the_calling_device_signed_in()
    {
        await using var factory = new ApiFactory();
        var phone = await factory.CreateAuthenticatedClientAsync();
        var desktop = await factory.CreateAuthenticatedClientAsync();
        var tablet = await factory.CreateAuthenticatedClientAsync();

        var response = await phone.PostAsync("/api/v1/auth/sessions/revoke-others", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<RevokedBody>(CaseInsensitive, TestContext.Current.CancellationToken))!.Revoked);

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await desktop.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Sessions_without_a_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/sessions", TestContext.Current.CancellationToken)).StatusCode);
    }

    private static readonly System.Text.Json.JsonSerializerOptions CaseInsensitive =
        new() { PropertyNameCaseInsensitive = true };

    private sealed record LoginResponseBody(string Token, string Username, DateTime? ExpiresAt);

    private sealed record SessionBody(Guid Id, DateTime CreatedAt, DateTime LastSeenAt, string? UserAgent, bool IsCurrent);

    private sealed record RevokedBody(int Revoked);
}
