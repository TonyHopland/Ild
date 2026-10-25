using ILD.Core.Services.Interfaces;

namespace ILD.Api.Middleware;

/// <summary>
/// Answers every API request 503 while a restore is replacing the instance's data. Any other
/// request would read half a restore or write into data that is about to be replaced. The restore
/// request itself is already past here.
/// </summary>
public sealed class MaintenanceMiddleware(RequestDelegate next, IMaintenanceState maintenance)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        // A WebSocket (a terminal) lasts as long as someone keeps it open and touches no data of the
        // restore's: it is refused once the restore has begun, never waited for.
        if (context.WebSockets.IsWebSocketRequest && !maintenance.IsActive)
        {
            await next(context);
            return;
        }

        // Held for the whole request, so a restore that begins meanwhile waits for it.
        using var work = maintenance.TryBeginWork();
        if (work is not null)
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsJsonAsync(new { error = "ILD is restoring a backup. Try again in a minute." });
    }
}
