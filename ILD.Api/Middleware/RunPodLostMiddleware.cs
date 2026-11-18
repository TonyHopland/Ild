using ILD.Core.Services.RunHosts;

namespace ILD.Api.Middleware;

/// <summary>
/// Answers a request that failed because the run's pod or container no longer exists with 409 and what to do, instead of a 500 or a raw status
/// from the cluster. The preview, the worktree tools and the chat's reads of a run's worktree all reach the pod, so all of them are told alike.
/// </summary>
public sealed class RunPodLostMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && RunPodLostException.In(ex) is { } lost)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = lost.Message, code = "run-pod-lost" });
        }
    }
}
