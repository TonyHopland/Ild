using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class WebhooksIntegrationTests
{
    [Fact]
    public async Task Forgejo_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Forgejo_with_token_but_no_secret_configured_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        // No RemoteProvider.WebhookSecret configured -> verifier rejects with 401.
        var response = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Forgejo_reaches_the_HMAC_check_with_either_principals_token()
    {
        // The bearer token here is whatever an operator pasted into the git host's
        // webhook settings, which may be the agent service token as readily as a
        // session token — so this route asks only for an authenticated caller and
        // leaves the real gate to HMAC. A 403 would mean a working webhook
        // configuration had been broken; the 401 is the signature check refusing.
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var agentToken = factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentToken);

        var response = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // The controller's own 401, not the authentication scheme's: the request
        // was let through and then refused by the signature check.
        Assert.DoesNotContain("No authentication token provided", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GitHub_with_token_but_no_secret_configured_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/github")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("X-GitHub-Event", "pull_request");
        request.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
