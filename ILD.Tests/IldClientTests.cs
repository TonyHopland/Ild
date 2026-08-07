using System.Net;
using ILD.McpServer;
using ModelContextProtocol;

namespace ILD.Tests;

/// <summary>
/// What an ILD tool tells the agent when a call does not succeed. The MCP host
/// replaces every exception that is not an <see cref="McpException"/> with
/// "An error occurred invoking '&lt;tool&gt;'", so whatever these messages omit
/// is unrecoverable: an agent that cannot see the status or the address cannot
/// tell a refused role from an expired token from a call that went to the wrong
/// ILD instance — which is exactly how a preview's MCP server pointing at the
/// host API stayed undiagnosed.
/// </summary>
public class IldClientTests
{
    private const string BaseAddress = "http://ild-host:8080/";

    private static IldClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(
            new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri(BaseAddress) },
            new IldClientOptions(BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null));

    [Fact]
    public async Task A_refusal_names_the_status_the_body_and_the_instance_that_refused()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"Unauthorized","message":"Invalid or expired session"}"""),
        });

        var ex = await Assert.ThrowsAsync<McpException>(
            () => client.GetRawAsync("api/v1/agent/workitems"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Invalid or expired session", ex.Message);
        Assert.Contains($"{BaseAddress}api/v1/agent/workitems", ex.Message);
    }

    [Fact]
    public async Task An_unreachable_API_names_the_address_it_tried_and_the_setting_that_chose_it()
    {
        var client = Client(_ => throw new HttpRequestException("Connection refused (ild-host:8080)"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => client.PostJsonAsync("api/v1/agent/workitems", new { title = "x" }));

        Assert.Contains($"{BaseAddress}api/v1/agent/workitems", ex.Message);
        Assert.Contains($"ILD_API_URL={BaseAddress.TrimEnd('/')}", ex.Message);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_call_stays_a_cancellation_rather_than_becoming_an_unreachable_API()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetRawAsync("api/v1/agent/workitems", cancelled.Token));
    }

    [Fact]
    public async Task A_successful_call_returns_the_body_verbatim()
    {
        const string payload = """[{"id":"1"}]""";
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });

        Assert.Equal(payload, await client.GetRawAsync("api/v1/agent/workitems"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
