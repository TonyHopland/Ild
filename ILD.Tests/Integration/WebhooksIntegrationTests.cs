using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace ILD.Tests.Integration;

public class WebhooksIntegrationTests
{
    [Fact]
    public async Task Forgejo_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forgejo_with_token_but_no_secret_configured_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        // No RemoteProvider.WebhookSecret configured -> verifier rejects with 401.
        var response = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
