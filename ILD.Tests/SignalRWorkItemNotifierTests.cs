using FluentAssertions;
using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Data.DTOs.SignalRPayloads;
using ILD.Data.Enums;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ILD.Tests;

public class SignalRWorkItemNotifierTests
{
    private static (Mock<IHubContext<WorkItemHub>> ctx, Mock<IClientProxy> proxy) BuildHubContext()
    {
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group("work-items")).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<WorkItemHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);
        return (ctx, proxy);
    }

    [Fact]
    public async Task WorkItemStateChangedAsync_sends_a_single_typed_payload()
    {
        var workItemId = Guid.NewGuid();
        var (ctx, proxy) = BuildHubContext();

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("WorkItemStateChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRWorkItemNotifier(ctx.Object);
        await notifier.WorkItemStateChangedAsync(workItemId, WorkItemStatus.Backlog, WorkItemStatus.WorkQueue);

        capturedArgs!.Should().HaveCount(1);
        var payload = capturedArgs[0].Should().BeOfType<WorkItemStateChangedPayload>().Subject;
        payload.WorkItemId.Should().Be(workItemId);
        payload.OldStatus.Should().Be(WorkItemStatus.Backlog);
        payload.NewStatus.Should().Be(WorkItemStatus.WorkQueue);
    }

    [Fact]
    public async Task HumanFeedbackRequiredAsync_sends_a_single_typed_payload()
    {
        var workItemId = Guid.NewGuid();
        var (ctx, proxy) = BuildHubContext();

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("HumanFeedbackRequired", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRWorkItemNotifier(ctx.Object);
        await notifier.HumanFeedbackRequiredAsync(workItemId, "Node failed");

        capturedArgs!.Should().HaveCount(1);
        var payload = capturedArgs[0].Should().BeOfType<HumanFeedbackRequiredPayload>().Subject;
        payload.WorkItemId.Should().Be(workItemId);
        payload.Reason.Should().Be("Node failed");
    }
}
