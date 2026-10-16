using System.Net;
using System.Reflection;
using System.Text.Json;
using ILD.McpServer;

namespace ILD.Tests;

/// <summary>
/// The MCP half of Loop Template Proposals, addressed by tool name and
/// parameter names — the schema the agent actually sees. Agents propose a whole
/// template document against the version they read and read what became of
/// their proposals; no tool saves, rejects or applies one.
/// </summary>
public class LoopTemplateProposalToolsTests
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
        var ild = new IldClient(
            new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) },
            new IldClientOptions(BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null));

        var method = typeof(IldClient).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Single(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Name == "McpServerToolAttribute"
                && a.NamedArguments.Any(n => n.MemberName == "Name" && (string?)n.TypedValue.Value == toolName)));
        var tools = Activator.CreateInstance(method.DeclaringType!, ild)!;
        var parameters = method.GetParameters();
        Assert.All(args.Keys, name => Assert.Contains(parameters, p => p.Name == name));
        var values = parameters
            .Select(p => args.TryGetValue(p.Name!, out var v) ? v : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();

        var result = await (Task<string>)method.Invoke(tools, values)!;
        return (result, seen!, body);
    }

    [Fact]
    public async Task propose_loop_template_change_posts_the_template_the_base_version_the_whole_document_and_the_rationale()
    {
        const string document = """{"$schema":"ild-loop-template/v2","name":"Release Loop","description":"","recoveryPolicy":"AutoResume","nodes":[],"edges":[]}""";
        var (result, request, body) = await InvokeToolAsync("propose_loop_template_change", new Dictionary<string, object?>
        {
            ["nameOrId"] = "Release Loop",
            ["baseVersion"] = 3,
            ["document"] = document,
            ["rationale"] = "Builds keep failing.",
        }, """{"id":"lp-1","templateId":"t-1","baseVersion":3,"status":"Pending"}""");

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{BaseAddress}api/v1/agent/loop-templates/proposals", request.RequestUri!.ToString());
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("Release Loop", json.GetProperty("nameOrId").GetString());
        Assert.Equal(3, json.GetProperty("baseVersion").GetInt32());
        Assert.Equal(document, json.GetProperty("document").GetString());
        Assert.Equal("Builds keep failing.", json.GetProperty("rationale").GetString());
        Assert.Contains("lp-1", result);
    }

    [Fact]
    public async Task list_loop_template_proposals_reads_the_callers_own_from_the_agent_surface()
    {
        var (result, request, _) = await InvokeToolAsync("list_loop_template_proposals", new Dictionary<string, object?>(),
            """[{"id":"lp-1","status":"Rejected","rejectionReason":"Keep one job."}]""");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BaseAddress}api/v1/agent/loop-templates/proposals", request.RequestUri!.ToString());
        Assert.Contains("Keep one job.", result);
    }

    [Fact]
    public void No_tool_saves_rejects_or_applies_a_loop_template_proposal()
    {
        Assert.Equal(
            new[] { "get_loop_template", "list_loop_template_proposals", "list_loop_templates", "propose_loop_template_change" },
            McpServerToolReflection.Names()
                .Where(n => n.Contains("loop_template", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
