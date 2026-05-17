using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Core.Services.Remote;
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
        var workItemId = Guid.NewGuid().ToString();
        var (ctx, proxy) = BuildHubContext();

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("WorkItemStateChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRWorkItemNotifier(ctx.Object);
        await notifier.WorkItemStateChangedAsync(workItemId, RemoteWorkItemStatus.Backlog, RemoteWorkItemStatus.WorkQueue);

        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<WorkItemStateChangedPayload>(capturedArgs![0]);
        Assert.Equal(workItemId, payload.WorkItemId);
        Assert.Equal(WorkItemStatus.Backlog, payload.OldStatus);
        Assert.Equal(WorkItemStatus.WorkQueue, payload.NewStatus);
    }

    [Fact]
    public async Task HumanFeedbackRequiredAsync_sends_a_single_typed_payload()
    {
        var workItemId = Guid.NewGuid().ToString();
        var (ctx, proxy) = BuildHubContext();

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("HumanFeedbackRequired", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRWorkItemNotifier(ctx.Object);
        await notifier.HumanFeedbackRequiredAsync(workItemId, "Node failed");

        Assert.Single(capturedArgs!);
        var payload = Assert.IsType<HumanFeedbackRequiredPayload>(capturedArgs![0]);
        Assert.Equal(workItemId, payload.WorkItemId);
        Assert.Equal("Node failed", payload.Reason);
    }
}
