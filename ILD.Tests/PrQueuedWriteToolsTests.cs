using System.Net;
using System.Reflection;
using ILD.McpServer;
using ILD.McpServer.Tools;

namespace ILD.Tests;

/// <summary>
/// The MCP half of an agent seeing and taking back what it queued for the pull
/// request, addressed by tool name and parameter names — the schema the agent
/// actually sees.
/// </summary>
public class PrQueuedWriteToolsTests
{
    private const string BaseAddress = "http://ild-host:8080/";

    private static async Task<(string Result, HttpRequestMessage Request)> InvokeToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> args, string responseBody)
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(req =>
        {
            seen = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) });
        });
        var tools = new PrReviewTools(new IldClient(
            new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) },
            new IldClientOptions(BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null)));

        var method = typeof(PrReviewTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Name == "McpServerToolAttribute"
                && a.NamedArguments.Any(n => n.MemberName == "Name" && (string?)n.TypedValue.Value == toolName)));
        var parameters = method.GetParameters();
        Assert.All(args.Keys, name => Assert.Contains(parameters, p => p.Name == name));
        var values = parameters
            .Select(p => args.TryGetValue(p.Name!, out var v) ? v : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();

        var result = await (Task<string>)method.Invoke(tools, values)!;
        return (result, seen!);
    }

    [Fact]
    public async Task withdraw_pr_write_deletes_the_one_write_it_names()
    {
        var (result, request) = await InvokeToolAsync("withdraw_pr_write", new Dictionary<string, object?>
        {
            ["workItemId"] = "wi-42",
            ["writeId"] = "a/b c",
        }, """{"ok":false,"writeId":"a/b c","message":"Not yours."}""");

        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(
            $"{BaseAddress}api/v1/agent/workitems/wi-42/pr-review/queue/a%2Fb%20c",
            request.RequestUri!.AbsoluteUri);
        Assert.Contains("Not yours.", result);
    }

    [Fact]
    public async Task list_queued_pr_writes_reads_the_queue_from_the_agent_surface()
    {
        var (result, request) = await InvokeToolAsync("list_queued_pr_writes",
            new Dictionary<string, object?> { ["workItemId"] = "wi-42" },
            """{"message":null,"writes":[{"id":"5a03cb617874","kind":"reply"}]}""");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/wi-42/pr-review/queue", request.RequestUri!.AbsoluteUri);
        Assert.Contains("5a03cb617874", result);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
