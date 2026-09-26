using System.Net;
using System.Reflection;
using System.Text.Json;
using ILD.McpServer;
using ILD.McpServer.Tools;

namespace ILD.Tests;

/// <summary>
/// The MCP half of Work Item Edit Proposals, addressed by tool name and
/// parameter names — the schema the agent actually sees. An omitted field must
/// stay omitted on the wire: the server reads a present-but-empty branch
/// override as "clear it", so a tool that filled in defaults would propose
/// changes the agent never asked for.
/// </summary>
public class WorkItemEditProposalToolsTests
{
    private const string BaseAddress = "http://ild-host:8080/";

    private static async Task<(string Result, HttpRequestMessage Request, string? Body)> InvokeToolAsync(
        string toolName, IReadOnlyDictionary<string, object?> args, string responseBody)
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new StubHandler(async req =>
        {
            seen = req;
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) };
        });
        var tools = new WorkItemTools(new IldClient(
            new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) },
            new IldClientOptions(BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null)));

        var method = typeof(WorkItemTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Name == "McpServerToolAttribute"
                && a.NamedArguments.Any(n => n.MemberName == "Name" && (string?)n.TypedValue.Value == toolName)));
        var parameters = method.GetParameters();
        Assert.All(args.Keys, name => Assert.Contains(parameters, p => p.Name == name));
        var values = parameters
            .Select(p => args.TryGetValue(p.Name!, out var v) ? v : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();

        var result = await (Task<string>)method.Invoke(tools, values)!;
        return (result, seen!, body);
    }

    [Fact]
    public async Task propose_workitem_edit_posts_only_the_fields_given_plus_the_rationale()
    {
        var (result, request, body) = await InvokeToolAsync("propose_workitem_edit", new Dictionary<string, object?>
        {
            ["id"] = "wi-42",
            ["description"] = "A sharper description.",
            ["baseBranchOverride"] = "",
            ["rationale"] = "The old description no longer matches the code.",
        }, """{"id":"p-1","status":"Pending"}""");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/wi-42/edit-proposals", request.RequestUri!.ToString());
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("A sharper description.", json.GetProperty("description").GetString());
        Assert.Equal("", json.GetProperty("baseBranchOverride").GetString());
        Assert.Equal("The old description no longer matches the code.", json.GetProperty("rationale").GetString());
        foreach (var omitted in new[] { "title", "tags", "branchNameOverride" })
            Assert.True(!json.TryGetProperty(omitted, out var v) || v.ValueKind == JsonValueKind.Null, $"{omitted} was sent");
        Assert.Contains("p-1", result);
    }

    [Fact]
    public async Task propose_workitem_edit_carries_tags_as_given()
    {
        var (_, _, body) = await InvokeToolAsync("propose_workitem_edit", new Dictionary<string, object?>
        {
            ["id"] = "wi-42",
            ["tags"] = new[] { "HIL", "bugfix" },
        }, "{}");

        var tags = JsonDocument.Parse(body!).RootElement.GetProperty("tags");
        Assert.Equal(new[] { "HIL", "bugfix" }, tags.EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public async Task list_workitem_edit_proposals_reads_the_items_proposals_from_the_agent_surface()
    {
        const string listed = """[{"id":"p-1","status":"Rejected","rejectionReason":"Too vague."}]""";

        var (result, request, _) = await InvokeToolAsync("list_workitem_edit_proposals",
            new Dictionary<string, object?> { ["id"] = "wi-42" }, listed);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BaseAddress}api/v1/agent/workitems/wi-42/edit-proposals", request.RequestUri!.ToString());
        Assert.Contains("Too vague.", result);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
