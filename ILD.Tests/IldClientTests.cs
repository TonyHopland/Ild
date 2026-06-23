using System.Net;
using System.Net.Http.Headers;
using ILD.McpServer;

namespace ILD.Tests;

/// <summary>
/// Unit coverage for <see cref="IldClient"/>'s SPA-fallback guard. The ILD API
/// ends its pipeline with <c>MapFallbackToFile("index.html")</c>, so a brand-new
/// agent-API route hitting a server running an older build returns the SPA shell
/// (<c>index.html</c>) with HTTP 200 rather than a 404. The agent API only ever
/// speaks JSON, so an HTML 200 must surface as a clear error — never be handed
/// back to the model as data.
/// </summary>
public sealed class IldClientTests
{
    private sealed class ResponderHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public ResponderHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }

    private static IldClient ClientReturning(HttpStatusCode status, string body, string mediaType)
    {
        var handler = new ResponderHandler(_ =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return resp;
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        return new IldClient(http, new IldClientOptions("http://localhost:5000", "tok", null));
    }

    [Fact]
    public async Task GetRawAsync_returns_a_json_body_unchanged()
    {
        var client = ClientReturning(HttpStatusCode.OK, "{\"$schema\":\"ild-loop-template/v1\"}", "application/json");

        var result = await client.GetRawAsync("api/v1/agent/current-loop");

        Assert.Equal("{\"$schema\":\"ild-loop-template/v1\"}", result);
    }

    [Fact]
    public async Task GetRawAsync_rejects_an_html_200_from_the_spa_fallback_by_content_type()
    {
        // A stale build serves index.html with text/html and a 200.
        var client = ClientReturning(HttpStatusCode.OK, "<!doctype html><html><body>app</body></html>", "text/html");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetRawAsync("api/v1/agent/current-loop"));

        Assert.Contains("HTML page", ex.Message);
        Assert.Contains("stale build", ex.Message);
        // The HTML itself must never leak through as if it were data.
        Assert.DoesNotContain("<body>app</body>", ex.Message);
    }

    [Fact]
    public async Task GetRawAsync_rejects_an_html_200_detected_by_body_sniff()
    {
        // Even when the content-type is unhelpful, the leading <!doctype html
        // marks the SPA shell.
        var client = ClientReturning(HttpStatusCode.OK, "\n  <!DOCTYPE html>\n<html></html>", "text/plain");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetRawAsync("api/v1/agent/current-loop"));

        Assert.Contains("HTML page", ex.Message);
    }

    [Fact]
    public async Task PutJsonAsync_rejects_an_html_200_from_the_spa_fallback()
    {
        var client = ClientReturning(HttpStatusCode.OK, "<html><head></head></html>", "text/html");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PutJsonAsync("api/v1/agent/current-loop", new { document = "{}" }));

        Assert.Contains("HTML page", ex.Message);
    }

    [Fact]
    public async Task GetRawAsync_propagates_a_non_success_status_with_its_body()
    {
        var client = ClientReturning(HttpStatusCode.BadRequest, "{\"error\":\"nope\"}", "application/json");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetRawAsync("api/v1/agent/current-loop"));

        Assert.Contains("400", ex.Message);
        Assert.Contains("nope", ex.Message);
    }
}
