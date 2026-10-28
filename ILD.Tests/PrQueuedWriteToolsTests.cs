using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>
/// The MCP half of an agent seeing and taking back what it queued for the pull
/// request, addressed by tool name and parameter names — the schema the agent
/// actually sees.
/// </summary>
public class PrQueuedWriteToolsTests
{
    [Fact]
    public async Task withdraw_pr_write_deletes_the_one_write_it_names()
    {
        var (result, request, _) = await McpToolInvoker.InvokeAsync("withdraw_pr_write", new Dictionary<string, object?>
        {
            ["workItemId"] = "wi-42",
            ["writeId"] = "a/b c",
        }, """{"ok":false,"writeId":"a/b c","message":"Not yours."}""");

        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(
            $"{McpToolInvoker.BaseAddress}api/v1/agent/workitems/wi-42/pr-review/queue/a%2Fb%20c",
            request.RequestUri!.AbsoluteUri);
        Assert.Contains("Not yours.", result);
    }

    [Fact]
    public async Task list_queued_pr_writes_reads_the_queue_from_the_agent_surface()
    {
        var (result, request, _) = await McpToolInvoker.InvokeAsync("list_queued_pr_writes",
            new Dictionary<string, object?> { ["workItemId"] = "wi-42" },
            """{"message":null,"writes":[{"id":"5a03cb617874","kind":"reply"}]}""");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{McpToolInvoker.BaseAddress}api/v1/agent/workitems/wi-42/pr-review/queue", request.RequestUri!.AbsoluteUri);
        Assert.Contains("5a03cb617874", result);
    }
}
