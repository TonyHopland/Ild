using System.Net;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests.Integration;

/// <summary>
/// Boots the real ILD API pipeline to pin where the preview proxy sits in it.
/// Ordering is the whole feature here: the proxy has to run before anything that
/// would claim a preview request or rewrite its response — the SPA's static files
/// and fallback, authentication, CORS, and the security headers — while still
/// leaving every non-preview request to travel the pipeline exactly as before.
/// The forwarding itself is covered by <see cref="PreviewProxyIntegrationTests"/>.
///
/// <para>
/// These tests only mean anything because <c>ILD.Tests</c> ships a stand-in
/// <c>wwwroot</c> to its output (see the csproj): the API's static-file branch is
/// conditional on that directory existing, so without it the pipeline under test
/// is a different shape from the one in the container — which is exactly how a
/// preview host being answered by ILD's own index.html went unnoticed.
/// </para>
/// </summary>
public class PreviewProxyPipelineTests
{
    private const string BaseHost = "ild.test";
    private const string PreviewHost = "wi-1." + BaseHost;

    private static ApiFactory FactoryWithProxyBase()
        => new(new Dictionary<string, string?> { [PreviewProxyBase.ConfigurationKey] = $"http://{BaseHost}" });

    private static HttpRequestMessage Request(string path, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        return request;
    }

    [Theory]
    [InlineData("/")]                 // the SPA's own entry point
    [InlineData("/assets/app.js")]    // a root-absolute asset path that collides with the bundle
    [InlineData("/api/v1/loopruns")]
    public async Task A_preview_hostname_is_answered_by_the_proxy_and_never_by_the_ILD_UI(string path)
    {
        using var factory = FactoryWithProxyBase();
        using var client = factory.CreateClient();

        // No bearer token: were the proxy behind authentication this would be a 401,
        // and were it behind the static files it would be ILD's own SPA.
        using var response = await client.SendAsync(Request(path, PreviewHost));
        var body = await response.Content.ReadAsStringAsync();

        // No preview is running, so the proxy's own 404 — not the SPA, and not a 401.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("No worktree preview is running at this address.", body);
        Assert.DoesNotContain("ILD-UI-SPA-MARKER", body);
        Assert.DoesNotContain("ILD-UI-BUNDLE-MARKER", body);
    }

    [Fact]
    public async Task A_preview_response_carries_none_of_the_ILD_UIs_own_headers()
    {
        using var factory = FactoryWithProxyBase();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request("/", PreviewHost));

        // The UI's CSP is deliberately `default-src 'self'`, which is right for ILD
        // and wrong for somebody else's application — a preview that loads a font or
        // a CDN script would be broken by inheriting it.
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.False(response.Headers.Contains("X-Frame-Options"));
    }

    [Fact]
    public async Task The_apex_host_still_gets_the_ILD_UI_and_its_security_headers()
    {
        using var factory = FactoryWithProxyBase();
        using var client = factory.CreateClient();

        using var spa = await client.SendAsync(Request("/", BaseHost));
        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Contains("ILD-UI-SPA-MARKER", await spa.Content.ReadAsStringAsync());
        Assert.True(spa.Headers.Contains("Content-Security-Policy"));

        using var bundle = await client.SendAsync(Request("/assets/app.js", BaseHost));
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        Assert.Contains("ILD-UI-BUNDLE-MARKER", await bundle.Content.ReadAsStringAsync());
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

        using var api = await client.SendAsync(Request("/api/v1/loopruns", PreviewHost));
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);

        using var spa = await client.SendAsync(Request("/", PreviewHost));
        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
        Assert.Contains("ILD-UI-SPA-MARKER", await spa.Content.ReadAsStringAsync());
    }
}
