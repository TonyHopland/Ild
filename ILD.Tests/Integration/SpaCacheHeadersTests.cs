using System.Net;

namespace ILD.Tests.Integration;

/// <summary>
/// A release only reaches a browser on a normal reload if the shell is
/// revalidated every time; the content-hashed bundle it names can then be cached
/// for good, since a new build names new files.
/// </summary>
public class SpaCacheHeadersTests(SharedApiFactory factory) : IClassFixture<SharedApiFactory>
{
    [Theory]
    [InlineData("/")]                    // UseDefaultFiles -> index.html
    [InlineData("/index.html")]
    [InlineData("/workitems")]           // an SPA route: served by the fallback
    [InlineData("/assets/some-route")]   // under assets/, but no such file: the fallback's shell
    public async Task The_SPA_shell_is_revalidated_before_every_reuse(string path)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ILD-UI-SPA-MARKER", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertMustRevalidate(response);
        Assert.True(
            response.Headers.ETag is not null || response.Content.Headers.LastModified is not null,
            "the shell needs a validator so a revalidation can be answered 304");
    }

    [Fact]
    public async Task A_static_file_outside_assets_is_revalidated_before_every_reuse()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/favicon.svg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertMustRevalidate(response);
    }

    [Fact]
    public async Task A_build_asset_is_cached_for_a_year_as_immutable()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/assets/app.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.Public, $"Cache-Control: {cacheControl}");
        Assert.Equal(TimeSpan.FromSeconds(31536000), cacheControl.MaxAge);
        Assert.Contains(cacheControl.Extensions, e => e.Name.Equals("immutable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_api_response_gets_no_long_lived_caching()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNotLongCached(response);
    }

    private static void AssertMustRevalidate(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.NoCache || cacheControl.NoStore, $"Cache-Control: {cacheControl}");
        AssertNotLongCached(response);
    }

    private static void AssertNotLongCached(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        if (cacheControl is null)
        {
            return;
        }
        Assert.True(cacheControl.MaxAge is null || cacheControl.MaxAge == TimeSpan.Zero, $"Cache-Control: {cacheControl}");
        Assert.True(cacheControl.SharedMaxAge is null || cacheControl.SharedMaxAge == TimeSpan.Zero, $"Cache-Control: {cacheControl}");
        Assert.DoesNotContain(cacheControl.Extensions, e => e.Name.Equals("immutable", StringComparison.OrdinalIgnoreCase));
    }
}
