using ILD.Api.Configuration;
using ILD.Api.Hubs;
using ILD.Core.Services.Remote;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ILD.Tests;

public class SignalRHumanInputProposalsTests
{
    [Fact]
    public async Task HumanInputProposalsChangedAsync_hints_the_work_item_group_with_the_work_item_id()
    {
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group("work-items")).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<WorkItemHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);
        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("HumanInputProposalsChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        await new SignalRWorkItemNotifier(ctx.Object).HumanInputProposalsChangedAsync("WI-7");

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.Single(capturedArgs!), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("WI-7", payload.GetProperty("workItemId").GetString());
    }
}
