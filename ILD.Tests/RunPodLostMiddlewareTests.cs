using ILD.Api.Middleware;
using ILD.Core.Services.RunHosts;
using Microsoft.AspNetCore.Http;

namespace ILD.Tests;

public sealed class RunPodLostMiddlewareTests
{
    private static async Task<DefaultHttpContext> RunAsync(Exception failure)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new RunPodLostMiddleware(_ => throw failure).InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task A_lost_run_pod_behind_a_request_is_answered_409_with_what_to_do_even_when_wrapped()
    {
        var context = await RunAsync(new InvalidOperationException("the tool failed", new RunPodLostException(Guid.NewGuid(), RunTermination.LostText("pod"))));

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("The run pod no longer exists", body);
        Assert.Contains("run-pod-lost", body);
    }

    [Fact]
    public async Task Any_other_failure_is_left_to_propagate()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new InvalidOperationException("other")));
}
