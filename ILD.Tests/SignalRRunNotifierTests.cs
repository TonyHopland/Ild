using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests;

public class SignalRRunNotifierTests
{
    private static (Mock<IHubContext<LoopRunHub>> ctx, Mock<IClientProxy> proxy, Mock<ILogger<SignalRRunNotifier>> logger) BuildHubContext(string expectedGroup)
    {
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(expectedGroup)).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<LoopRunHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);
        var logger = new Mock<ILogger<SignalRRunNotifier>>();
        return (ctx, proxy, logger);
    }

    [Fact]
    public async Task NodeStateChangedAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var (ctx, proxy, logger) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("NodeStateChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object, logger.Object);
        await notifier.NodeStateChangedAsync(runId, nodeId, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running);

        Assert.NotNull(capturedArgs);
        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<NodeStateChangedPayload>(capturedArgs[0]);
        Assert.Equal(runId, payload.RunId);
        Assert.Equal(nodeId, payload.NodeId);
        Assert.Equal(LoopRunNodeStatus.Pending, payload.OldStatus);
        Assert.Equal(LoopRunNodeStatus.Running, payload.NewStatus);
    }

    [Fact]
    public async Task RunStateChangedAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var (ctx, proxy, logger) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("LoopRunStateChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object, logger.Object);
        await notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Completed);

        Assert.NotNull(capturedArgs);
        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<LoopRunStateChangedPayload>(capturedArgs![0]);
        Assert.Equal(runId, payload.RunId);
        Assert.Equal(LoopRunStatus.Completed, payload.NewStatus);
    }

    [Fact]
    public async Task EventLoggedAsync_sends_the_events_identity_to_the_runs_group()
    {
        var runId = Guid.NewGuid();
        var (ctx, proxy, logger) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("EventLogged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object, logger.Object);
        var nodeId = Guid.NewGuid();
        var runNodeId = Guid.NewGuid();
        var at = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        await notifier.EventLoggedAsync(runId, 42, "NodeStarted", nodeId, runNodeId, at);

        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<EventLoggedPayload>(capturedArgs![0]);
        Assert.Equal(runId, payload.RunId);
        Assert.Equal(42, payload.Id);
        Assert.Equal("NodeStarted", payload.EventType);
        Assert.Equal(nodeId, payload.NodeId);
        Assert.Equal(runNodeId, payload.RunNodeId);
        Assert.Equal(at, payload.Timestamp);
    }

    [Fact]
    public async Task NodeProgressAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var (ctx, proxy, logger) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("NodeProgress", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object, logger.Object);
        await notifier.NodeProgressAsync(runId, nodeId, "thinking about the problem...", 7);

        Assert.NotNull(capturedArgs);
        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<NodeProgressPayload>(capturedArgs[0]);
        Assert.Equal(runId, payload.RunId);
        Assert.Equal(nodeId, payload.NodeId);
        Assert.Equal("thinking about the problem...", payload.Line);
        Assert.Equal(7, payload.Seq);
    }
}
