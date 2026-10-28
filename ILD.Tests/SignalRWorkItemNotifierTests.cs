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
    public async Task WorkItemEditProposalsChangedAsync_hints_the_work_item_group_with_the_item_id()
    {
        var workItemId = Guid.NewGuid().ToString();
        var (ctx, proxy) = BuildHubContext();

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("WorkItemEditProposalsChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRWorkItemNotifier(ctx.Object);
        await notifier.WorkItemEditProposalsChangedAsync(workItemId);

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.Single(capturedArgs!), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(workItemId, payload.GetProperty("workItemId").GetString());
    }

    [Fact]
    public async Task GlobalVariableProposalsChangedAsync_hints_the_work_item_group_with_the_variable_name()
    {
        var (ctx, proxy) = BuildHubContext();

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("GlobalVariableProposalsChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRWorkItemNotifier(ctx.Object);
        await notifier.GlobalVariableProposalsChangedAsync("review_rules");

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.Single(capturedArgs!), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("review_rules", payload.GetProperty("name").GetString());
    }
}
