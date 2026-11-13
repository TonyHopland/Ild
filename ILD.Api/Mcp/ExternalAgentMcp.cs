using System.Net;
using ILD.Api.Authentication;
using ILD.Api.Middleware;
using ILD.McpServer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace ILD.Api.Mcp;

/// <summary>
/// The Streamable-HTTP MCP endpoint external agents use (ADR-0037). It serves the same tool classes
/// ILD's own agents get over stdio, minus those that need a run or a chat. A tool call reaches the
/// agent API by looping back to this process with the caller's own bearer token, so every rule the REST
/// surface enforces — ownership, read-only grants, proposals, rate limit, audit — applies unchanged.
/// The transport is stateless: each request authenticates on its own, so a revoked grant stops at once.
/// </summary>
public static class ExternalAgentMcp
{
    public const string Path = "/mcp";
    public const string LoopbackClient = "ild-mcp-loopback";
    public const string LoopbackUrlSetting = "Mcp:LoopbackUrl";

    public static IServiceCollection AddIldMcpEndpoint(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddHttpClient(LoopbackClient);
        services.AddScoped(sp =>
        {
            var context = sp.GetRequiredService<IHttpContextAccessor>().HttpContext
                ?? throw new InvalidOperationException("The MCP tools run inside an HTTP request.");
            var token = BearerToken(context.Request);
            var apiUrl = LoopbackUrl(sp);
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(LoopbackClient);
            http.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Add(ExternalAgentAuditMiddleware.ViaHeader, ExternalAgentAuditMiddleware.ViaMcp);
            return new IldClient(http, new IldClientOptions(apiUrl, token, LoopRunId: null));
        });

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "ild", Version = typeof(ExternalAgentMcp).Assembly.GetName().Version?.ToString() ?? "0" };
            })
            .WithHttpTransport(options => options.Stateless = true)
            // Named: the unnamed call binds the generic WithTools<T>(target) overload, which registers no tool at all.
            .WithTools(toolTypes: ExternalAgentTools.ToolTypes);
        return services;
    }

    public static IEndpointConventionBuilder MapIldMcpEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapMcp(Path).RequireAuthorization(IldAuthentication.AgentSurfacePolicy);

    private static string BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : header.Trim();
    }

    /// <summary>
    /// Where this process answers: the configured <see cref="LoopbackUrlSetting"/>, else the first plain-HTTP
    /// address it listens on, with a wildcard host read as the loopback address.
    /// </summary>
    private static string LoopbackUrl(IServiceProvider services)
    {
        if (services.GetRequiredService<IConfiguration>()[LoopbackUrlSetting] is { Length: > 0 } configured)
            return configured;

        var addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var listening = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The API listens on no plain-HTTP address for the MCP tools to call back; set " + LoopbackUrlSetting + ".");
        var uri = new Uri(listening.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"));
        var host = uri.Host is "0.0.0.0" or "[::]" or "::" ? IPAddress.Loopback.ToString() : uri.Host;
        return $"http://{host}:{uri.Port}";
    }
}
