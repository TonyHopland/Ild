using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

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

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = await response.Content.ReadFromJsonAsync<LoginResponseBody>(jsonOptions);
        body!.Token.Should().NotBeNullOrWhiteSpace();
        body.Username.Should().Be("admin");
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

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_valid_token_returns_200()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record LoginResponseBody(string Token, string Username);
}
