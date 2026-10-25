using ILD.Core.Services.Interfaces;

namespace ILD.Api.Middleware;

/// <summary>
/// Answers every API request 503 while a restore is replacing the instance's data. Any other
/// request would read half a restore or write into data that is about to be replaced. The restore
/// request itself is already past here.
/// </summary>
public sealed class MaintenanceMiddleware(RequestDelegate next, IMaintenanceState maintenance)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (!maintenance.IsActive || !context.Request.Path.StartsWithSegments("/api"))
            return next(context);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "30";
        return context.Response.WriteAsJsonAsync(new { error = "ILD is restoring a backup. Try again in a minute." });
    }
}
