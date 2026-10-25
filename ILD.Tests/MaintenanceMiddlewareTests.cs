using ILD.Api.Middleware;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace ILD.Tests;

public sealed class MaintenanceMiddlewareTests
{
    private sealed class Sockets(bool isWebSocket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => isWebSocket;
        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context) => throw new NotSupportedException();
    }

    /// <summary>Starts a request that stays open and says whether a restore would have to wait for it.</summary>
    private static async Task<bool> RestoreWaitsForAsync(bool isWebSocket)
    {
        var state = new MaintenanceState(TimeSpan.Zero);
        var open = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var middleware = new MaintenanceMiddleware(_ =>
        {
            entered.SetResult();
            return open.Task;
        }, state);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/anything";
        context.Features.Set<IHttpWebSocketFeature>(new Sockets(isWebSocket));

        var request = middleware.InvokeAsync(context);
        await entered.Task;
        var idle = await state.WaitForWorkAsync(allowed: 0, TestContext.Current.CancellationToken);
        open.SetResult();
        await request;
        return !idle;
    }

    [Fact]
    public async Task A_restore_waits_for_an_ordinary_request_but_not_for_an_open_WebSocket()
    {
        Assert.True(await RestoreWaitsForAsync(isWebSocket: false));
        Assert.False(await RestoreWaitsForAsync(isWebSocket: true));
    }

    [Fact]
    public async Task During_a_restore_a_WebSocket_is_refused_like_any_other_request()
    {
        var state = new MaintenanceState();
        var middleware = new MaintenanceMiddleware(_ => throw new InvalidOperationException("must not run"), state);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/anything";
        context.Features.Set<IHttpWebSocketFeature>(new Sockets(true));
        context.Response.Body = new MemoryStream();

        using (state.Enter())
            await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }
}
