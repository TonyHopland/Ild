using System.Net;
using System.Net.Http.Headers;
using ILD.McpServer;
using ILD.McpServer.Tools;

namespace ILD.Tests;

/// <summary>
/// Unit coverage for <see cref="LoopTools"/> error surfacing (loop editor context,
/// ADR-0011). The MCP SDK masks an exception thrown inside a tool method as a
/// generic, detail-free "An error occurred invoking '…'", so a read/write that
/// fails at the transport layer — a route missing on a stale build, the API
/// unreachable — is impossible to diagnose. These tools must catch the failure and
/// RETURN the reason as text so the agent and the human see the actual cause.
/// </summary>
public sealed class LoopToolsTests
{
    private sealed class ResponderHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public ResponderHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }

    private static LoopTools ToolsReturning(HttpStatusCode status, string body, string mediaType)
    {
        var handler = new ResponderHandler(_ =>
        {
            var resp = new HttpResponseMessage(status) { Content = new StringContent(body) };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return resp;
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var client = new IldClient(http, new IldClientOptions("http://localhost:5000", "tok", null, "chat-1"));
        return new LoopTools(client);
    }

    [Fact]
    public async Task GetCurrentLoop_returns_the_document_on_success()
    {
        var tools = ToolsReturning(HttpStatusCode.OK, "{\"$schema\":\"ild-loop-template/v1\"}", "application/json");

        var result = await tools.GetCurrentLoop();

        Assert.Equal("{\"$schema\":\"ild-loop-template/v1\"}", result);
    }

    [Fact]
    public async Task GetCurrentLoop_returns_a_readable_error_instead_of_throwing_on_the_spa_fallback()
    {
        // A stale build serves index.html (200) for the not-yet-deployed route.
        var tools = ToolsReturning(HttpStatusCode.OK, "<!doctype html><html></html>", "text/html");

        // Must NOT throw — a thrown exception would be masked by the SDK as a
        // detail-free generic error.
        var result = await tools.GetCurrentLoop();

        Assert.StartsWith("[ild-error]", result);
        Assert.Contains("read the current loop", result);
        Assert.Contains("HTML page", result);
        Assert.Contains("stale build", result);
    }

    [Fact]
    public async Task GetCurrentLoop_returns_a_readable_error_on_a_server_error()
    {
        var tools = ToolsReturning(HttpStatusCode.InternalServerError, "boom", "text/plain");

        var result = await tools.GetCurrentLoop();

        Assert.StartsWith("[ild-error]", result);
        Assert.Contains("500", result);
    }

    [Fact]
    public async Task UpdateCurrentLoop_returns_the_ack_on_success()
    {
        var tools = ToolsReturning(HttpStatusCode.Accepted, "{\"accepted\":true}", "application/json");

        var result = await tools.UpdateCurrentLoop("{\"$schema\":\"ild-loop-template/v1\"}");

        Assert.Equal("{\"accepted\":true}", result);
    }

    [Fact]
    public async Task UpdateCurrentLoop_returns_a_readable_error_instead_of_throwing_on_the_spa_fallback()
    {
        var tools = ToolsReturning(HttpStatusCode.OK, "<html><body>app</body></html>", "text/html");

        var result = await tools.UpdateCurrentLoop("{\"$schema\":\"ild-loop-template/v1\"}");

        Assert.StartsWith("[ild-error]", result);
        Assert.Contains("apply the loop edit", result);
        Assert.Contains("HTML page", result);
    }
}
