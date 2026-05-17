using System.Net;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
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
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = await response.Content.ReadFromJsonAsync<LoginResponseBody>(jsonOptions);
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
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_valid_token_returns_200()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record LoginResponseBody(string Token, string Username);
}
