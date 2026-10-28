using System.Globalization;
using System.Net;
using System.Reflection;
using ILD.McpServer;
using ModelContextProtocol.Server;

namespace ILD.Tests.Fakes;

/// <summary>
/// Calls an ILD MCP server tool by the name and parameter names the agent sees, against an
/// <see cref="IldClient"/> whose HTTP side is stubbed, and returns what the tool answered and
/// the one request it sent.
/// </summary>
internal static class McpToolInvoker
{
    public const string BaseAddress = "http://ild-host:8080/";

    public static async Task<(string Result, HttpRequestMessage Request, string? Body)> InvokeAsync(
        string toolName, IReadOnlyDictionary<string, object?> args, string responseBody = "{}")
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
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var tools = Activator.CreateInstance(method.DeclaringType!, ild)!;
        var parameters = method.GetParameters();
        Assert.All(args.Keys, name => Assert.Contains(parameters, p => p.Name == name));
        var values = parameters
            .Select(p => args.TryGetValue(p.Name!, out var v) ? ConvertTo(v, p.ParameterType) : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();

        var result = await (Task<string>)method.Invoke(tools, values)!;
        Assert.NotNull(seen);
        return (result, seen, body);
    }

    private static object? ConvertTo(object? value, Type type)
    {
        if (value is null) return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsInstanceOfType(value)) return value;
        return target == typeof(string)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }
}
