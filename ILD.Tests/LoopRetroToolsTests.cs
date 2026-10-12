using System.Net;
using System.Reflection;
using System.Web;
using ILD.McpServer;

namespace ILD.Tests;

/// <summary>
/// The MCP half of the loop-retro read tools, addressed by tool name and
/// parameter names — the schema the agent actually sees: each tool reads the
/// agent route it wraps and passes every filter through.
/// </summary>
public class LoopRetroToolsTests
{
    private const string BaseAddress = "http://ild-host:8080/";

    private static async Task<HttpRequestMessage> InvokeToolAsync(string toolName, IReadOnlyDictionary<string, object?> args)
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(req =>
        {
            seen = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        });
        var ild = new IldClient(
            new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) },
            new IldClientOptions(BaseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null));

        var method = typeof(ILD.McpServer.Tools.ListingTools).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Single(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Name == "McpServerToolAttribute"
                && a.NamedArguments.Any(n => n.MemberName == "Name" && (string?)n.TypedValue.Value == toolName)));
        var tools = Activator.CreateInstance(method.DeclaringType!, ild)!;
        var parameters = method.GetParameters();
        Assert.All(args.Keys, name => Assert.Contains(parameters, p => p.Name == name));
        var values = parameters
            .Select(p => args.TryGetValue(p.Name!, out var v) ? ConvertTo(v, p.ParameterType) : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();

        await (Task<string>)method.Invoke(tools, values)!;
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Get, seen.Method);
        return seen;
    }

    private static object? ConvertTo(object? value, Type type)
    {
        if (value is null) return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target == typeof(string) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string Path, Dictionary<string, string?> Query) Split(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var query = HttpUtility.ParseQueryString(uri.Query);
        return (uri.AbsolutePath, query.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => query[k]));
    }

    [Fact]
    public async Task get_template_analytics_passes_every_filter_to_the_analytics_route()
    {
        var templateId = Guid.NewGuid().ToString();
        var (path, query) = Split(await InvokeToolAsync("get_template_analytics", new Dictionary<string, object?>
        {
            ["from"] = "2026-06-01",
            ["to"] = "2026-06-07",
            ["provider"] = "claude",
            ["templateId"] = templateId,
        }));

        Assert.Equal("/api/v1/agent/analytics/templates", path);
        Assert.Equal("2026-06-01", query["from"]);
        Assert.Equal("2026-06-07", query["to"]);
        Assert.Equal("claude", query["provider"]);
        Assert.Equal(templateId, query["templateId"]);

        var (barePath, bareQuery) = Split(await InvokeToolAsync("get_template_analytics", new Dictionary<string, object?>()));
        Assert.Equal("/api/v1/agent/analytics/templates", barePath);
        Assert.All(bareQuery.Values, Assert.Null);
    }

    [Fact]
    public async Task get_loop_template_sends_a_name_with_slashes_and_spaces_as_a_query_value()
    {
        const string name = "Team/Retro loop";
        var (path, query) = Split(await InvokeToolAsync("get_loop_template", new Dictionary<string, object?>
        {
            ["nameOrId"] = name,
            ["version"] = 2,
        }));

        Assert.Equal("/api/v1/agent/loop-templates/document", path);
        Assert.Equal(name, query["nameOrId"]);
        Assert.Equal("2", query["version"]);

        var (_, latest) = Split(await InvokeToolAsync("get_loop_template", new Dictionary<string, object?> { ["nameOrId"] = name }));
        Assert.Equal(name, latest["nameOrId"]);
        Assert.False(latest.TryGetValue("version", out var version) && !string.IsNullOrEmpty(version));
    }

    [Fact]
    public async Task get_loop_run_reads_the_one_run()
    {
        var runId = Guid.NewGuid().ToString();
        var (path, _) = Split(await InvokeToolAsync("get_loop_run", new Dictionary<string, object?> { ["runId"] = runId }));

        Assert.Equal($"/api/v1/agent/loop-runs/{runId}", path);
    }

    [Fact]
    public async Task list_loop_runs_passes_the_date_and_template_filters_with_the_existing_ones()
    {
        var templateId = Guid.NewGuid().ToString();
        var (path, query) = Split(await InvokeToolAsync("list_loop_runs", new Dictionary<string, object?>
        {
            ["workItemId"] = "WI-7",
            ["from"] = "2026-06-01",
            ["to"] = "2026-06-07",
            ["templateId"] = templateId,
            ["skip"] = 10,
            ["take"] = 20,
        }));

        Assert.Equal("/api/v1/agent/loop-runs", path);
        Assert.Equal("WI-7", query["workItemId"]);
        Assert.Equal("2026-06-01", query["from"]);
        Assert.Equal("2026-06-07", query["to"]);
        Assert.Equal(templateId, query["templateId"]);
        Assert.Equal("10", query["skip"]);
        Assert.Equal("20", query["take"]);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }
}
