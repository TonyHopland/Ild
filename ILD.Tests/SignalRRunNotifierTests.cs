using FluentAssertions;
using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ILD.Tests;

public class SignalRRunNotifierTests
{
    private static (Mock<IHubContext<LoopRunHub>> ctx, Mock<IClientProxy> proxy) BuildHubContext(string expectedGroup)
    {
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(expectedGroup)).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<LoopRunHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);
        return (ctx, proxy);
    }

    [Fact]
    public async Task NodeStateChangedAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var (ctx, proxy) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("NodeStateChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object);
        await notifier.NodeStateChangedAsync(runId, nodeId, LoopRunNodeStatus.Pending, LoopRunNodeStatus.Running);

        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().HaveCount(1, "SignalR should receive one typed payload, not positional args");
        var payload = capturedArgs[0].Should().BeOfType<NodeStateChangedPayload>().Subject;
        payload.RunId.Should().Be(runId);
        payload.NodeId.Should().Be(nodeId);
        payload.OldStatus.Should().Be(LoopRunNodeStatus.Pending);
        payload.NewStatus.Should().Be(LoopRunNodeStatus.Running);
    }

    [Fact]
    public async Task RunStateChangedAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var (ctx, proxy) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("LoopRunStateChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object);
        await notifier.RunStateChangedAsync(runId, LoopRunStatus.Running, LoopRunStatus.Completed);

        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().HaveCount(1);
        var payload = capturedArgs[0].Should().BeOfType<LoopRunStateChangedPayload>().Subject;
        payload.RunId.Should().Be(runId);
        payload.NewStatus.Should().Be(LoopRunStatus.Completed);
    }

    [Fact]
    public async Task EventLoggedAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var (ctx, proxy) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("EventLogged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object);
        await notifier.EventLoggedAsync(runId, "hello");

        capturedArgs!.Should().HaveCount(1);
        var payload = capturedArgs[0].Should().BeOfType<EventLoggedPayload>().Subject;
        payload.RunId.Should().Be(runId);
        payload.Message.Should().Be("hello");
    }

    [Fact]
    public async Task NodeProgressAsync_sends_a_single_typed_payload()
    {
        var runId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var (ctx, proxy) = BuildHubContext(runId.ToString());

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("NodeProgress", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRRunNotifier(ctx.Object);
        await notifier.NodeProgressAsync(runId, nodeId, "thinking about the problem...");

        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().HaveCount(1, "SignalR should receive one typed payload, not positional args");
        var payload = capturedArgs[0].Should().BeOfType<NodeProgressPayload>().Subject;
        payload.RunId.Should().Be(runId);
        payload.NodeId.Should().Be(nodeId);
        payload.Line.Should().Be("thinking about the problem...");
    }
}
