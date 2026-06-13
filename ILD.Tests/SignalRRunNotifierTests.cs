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
    public async Task EventLoggedAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var (ctx, proxy, logger) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("EventLogged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object, logger.Object);
        var nodeId = Guid.NewGuid();
        await notifier.EventLoggedAsync(runId, "hello", "NodeStarted", nodeId, null);

        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<EventLoggedPayload>(capturedArgs![0]);
        Assert.Equal(runId, payload.RunId);
        Assert.Equal("hello", payload.Message);
        Assert.Equal("NodeStarted", payload.EventType);
        Assert.Equal(nodeId, payload.NodeId);
        Assert.Null(payload.RunNodeId);
    }

    [Fact]
    public async Task EventLoggedAsync_includes_runNodeId_in_payload()
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
        await notifier.EventLoggedAsync(runId, "AI Node started", "NodeStarted", nodeId, runNodeId);

        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<EventLoggedPayload>(capturedArgs![0]);
        Assert.Equal(runId, payload.RunId);
        Assert.Equal(nodeId, payload.NodeId);
        Assert.Equal(runNodeId, payload.RunNodeId);
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
