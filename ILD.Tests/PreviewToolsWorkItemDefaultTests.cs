using System.Net;
using ILD.McpServer;
using ILD.McpServer.Tools;
using ILD.Tests.Fakes;
using ModelContextProtocol;

namespace ILD.Tests;

/// <summary>
/// An AI node in a loop run is not told its work item id, so the preview tools
/// take it from the run the MCP server was launched for when the agent omits it.
/// </summary>
public class PreviewToolsWorkItemDefaultTests
{
    private const string BaseAddress = "http://ild-host:8080/";

    private static (PreviewTools Tools, Func<string?> Seen) Tools(string? runWorkItemId)
    {
        string? seen = null;
        var http = new HttpClient(new StubHandler(req =>
        {
            seen = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        })) { BaseAddress = new Uri(BaseAddress) };
        var ild = new IldClient(http, new IldClientOptions(
            BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null, WorkItemId: runWorkItemId));
        return (new PreviewTools(ild), () => seen);
    }

    [Fact]
    public async Task An_omitted_id_is_the_work_item_of_the_run()
    {
        var (tools, seen) = Tools("run-wi");

        await tools.GetPreview();
        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/run-wi/preview", seen());

        await tools.StartPreview();
        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/run-wi/preview/start", seen());

        await tools.GetPreviewLogs("web");
        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/run-wi/preview/logs?service=web", seen());
    }

    [Fact]
    public async Task An_explicit_id_wins_over_the_work_item_of_the_run()
    {
        var (tools, seen) = Tools("run-wi");

        await tools.StopPreview("other-wi");

        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/other-wi/preview/stop", seen());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task No_id_and_no_work_item_of_the_run_is_an_error_the_agent_can_read(string? runWorkItemId)
    {
        var (tools, seen) = Tools(runWorkItemId);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.GetPreview());

        Assert.Contains("workItemId", ex.Message);
        Assert.Null(seen());
    }
}
