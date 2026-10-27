using System.Net;
using System.Reflection;
using System.Text.Json;
using ILD.McpServer;

namespace ILD.Tests;

/// <summary>
/// The MCP half of Human Input Proposals, addressed by tool name and parameter
/// names — the schema the agent actually sees.
/// </summary>
public class HumanInputProposalToolsTests
{
    private const string BaseAddress = "http://ild-host:8080/";

    private static MethodInfo ToolNamed(string toolName)
        => typeof(IldClient).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Single(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Name == "McpServerToolAttribute"
                && a.NamedArguments.Any(n => n.MemberName == "Name" && (string?)n.TypedValue.Value == toolName)));

    [Fact]
    public async Task propose_human_input_posts_the_output_the_text_and_the_rationale_for_the_escaped_work_item()
    {
        HttpRequestMessage? seen = null;
        string? body = null;
        var handler = new StubHandler(async req =>
        {
            seen = req;
            body = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"hp-1","status":"Pending"}""") };
        });
        var ild = new IldClient(
            new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) },
            new IldClientOptions(BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null));
        var method = ToolNamed("propose_human_input");
        var tools = Activator.CreateInstance(method.DeclaringType!, ild)!;
        var args = new Dictionary<string, object?>
        {
            ["workItemId"] = "WI 1/odd",
            ["output"] = "Rework",
            ["text"] = "Split it up.\nThen retry.",
            ["rationale"] = "The change is too wide.",
        };
        var values = method.GetParameters().Select(p => args[p.Name!]).ToArray();

        var result = await (Task<string>)method.Invoke(tools, values)!;

        Assert.Equal(HttpMethod.Post, seen!.Method);
        var uri = seen.RequestUri!.AbsoluteUri;
        Assert.StartsWith($"{BaseAddress}api/v1/agent/workitems/", uri);
        Assert.EndsWith($"{Uri.EscapeDataString("WI 1/odd")}/human-input-proposals", uri);
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("Rework", json.GetProperty("output").GetString());
        Assert.Equal("Split it up.\nThen retry.", json.GetProperty("text").GetString());
        Assert.Equal("The change is too wide.", json.GetProperty("rationale").GetString());
        Assert.Contains("hp-1", result);
    }

    [Fact]
    public void propose_human_input_takes_exactly_the_four_string_parameters_and_adds_no_other_tool()
    {
        var parameters = ToolNamed("propose_human_input").GetParameters();

        Assert.Equal(
            new[] { "workItemId", "output", "text", "rationale" }.OrderBy(n => n, StringComparer.Ordinal),
            parameters.Select(p => p.Name!).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(parameters, p => Assert.Equal(typeof(string), p.ParameterType));
        Assert.Equal(
            new[] { "propose_human_input" },
            McpServerToolReflection.Names().Where(n => n.Contains("human_input", StringComparison.Ordinal)));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
