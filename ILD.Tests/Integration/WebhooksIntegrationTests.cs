using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

public class WebhooksIntegrationTests(SharedApiFactory factory) : IClassFixture<SharedApiFactory>
{
    [Fact]
    public async Task Forgejo_with_token_but_no_secret_configured_returns_401()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        // No RemoteProvider.WebhookSecret configured -> verifier rejects with 401.
        var response = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo", new { }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Forgejo_reaches_the_HMAC_check_with_no_token_and_with_one_that_is_no_longer_valid()
    {
        var client = factory.CreateClient();
        var anonymous = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo", new { }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.DoesNotContain("No authentication token provided", await anonymous.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A webhook set up before, whose URL still carries a token that has since ended, keeps reaching the signature check.
        var response = await client.PostAsJsonAsync("/api/v1/webhooks/forgejo?access_token=ended", new { }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // The controller's own 401, not the authentication scheme's: the request
        // was let through and then refused by the signature check.
        Assert.DoesNotContain("No authentication token provided", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHub_with_token_but_no_secret_configured_returns_401()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/github")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("X-GitHub-Event", "pull_request");
        request.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
