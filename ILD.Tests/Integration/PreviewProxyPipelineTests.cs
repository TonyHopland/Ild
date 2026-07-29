using System.Net;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests.Integration;

/// <summary>
/// Boots the real ILD API pipeline to pin where the preview proxy sits in it: in
/// front of authentication (a preview cannot carry an ILD session token) and
/// behind a hostname check strict enough that the UI's own host never enters it.
/// The forwarding itself is covered by <see cref="PreviewProxyIntegrationTests"/>.
/// </summary>
public class PreviewProxyPipelineTests
{
    private const string BaseHost = "ild.test";

    private static ApiFactory FactoryWithProxyBase()
        => new(new Dictionary<string, string?> { [PreviewProxyBase.ConfigurationKey] = $"http://{BaseHost}" });

    private static HttpRequestMessage Request(string path, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        return request;
    }

    [Fact]
    public async Task A_preview_hostname_is_answered_by_the_proxy_before_authentication()
    {
        using var factory = FactoryWithProxyBase();
        using var client = factory.CreateClient();

        // No bearer token: were the proxy behind AuthMiddleware this would be a 401.
        using var response = await client.SendAsync(Request("/api/v1/loopruns", $"wi-1.{BaseHost}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_apex_host_still_reaches_the_api_and_is_still_authenticated()
    {
        using var factory = FactoryWithProxyBase();
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(Request("/api/v1/loopruns", BaseHost));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var authenticatedClient = await factory.CreateAuthenticatedClientAsync();
        using var authenticated = await authenticatedClient.SendAsync(Request("/api/v1/loopruns", BaseHost));
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task Without_the_proxy_base_a_preview_hostname_is_just_another_host()
    {
        // The default factory sets no ILD_PREVIEW_PROXY_BASE, which is the shipped
        // default: the proxy is inert and every request keeps its old behaviour.
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request("/api/v1/loopruns", $"wi-1.{BaseHost}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
