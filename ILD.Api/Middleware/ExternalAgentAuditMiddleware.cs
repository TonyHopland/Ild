using System.Net;
using System.Security.Claims;
using ILD.Api.Authentication;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Api.Middleware;

/// <summary>
/// Records every request an external-agent grant authenticated (ADR-0037): grant, method, path and the status it got,
/// the last of which is only known once the rest of the pipeline has answered. The query string is left out, so a
/// token a caller wrongly put there is not copied into the audit trail.
/// </summary>
public sealed class ExternalAgentAuditMiddleware(RequestDelegate next, ILogger<ExternalAgentAuditMiddleware> logger)
{
    public const string ViaHeader = "X-ILD-Via";
    public const string ViaMcp = "mcp";

    public async Task InvokeAsync(HttpContext context, IExternalAgentGrantService grants, TrustedProxies proxies)
    {
        await next(context);

        if (!context.User.IsInRole(IldAuthentication.ExternalAgentRole)
            || !Guid.TryParse(context.User.FindFirstValue(IldAuthentication.SessionIdClaim), out var grantId))
            return;

        var remote = context.Connection.RemoteIpAddress;
        var viaMcp = remote is not null && IPAddress.IsLoopback(remote)
            && context.Request.Headers[ViaHeader] == ViaMcp;
        var from = viaMcp ? null : proxies.ClientOf(remote, context.Request.Headers["X-Forwarded-For"])?.ToString();
        var path = context.Request.Path.Value ?? "/";

        logger.LogInformation("Grant {GrantId} {Method} {Path} -> {Status}", grantId, context.Request.Method, path, context.Response.StatusCode);
        try
        {
            await grants.RecordActivityAsync(grantId, context.Request.Method, path, context.Response.StatusCode, from, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the activity of grant {GrantId}", grantId);
        }
    }
}
