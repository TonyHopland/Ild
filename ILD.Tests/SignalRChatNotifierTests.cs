using ILD.Api.Configuration;
using ILD.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class SignalRChatNotifierTests
{
    [Fact]
    public async Task EditProposalsChangedAsync_hints_only_that_chats_group_with_its_id()
    {
        var chatSessionId = Guid.NewGuid();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(chatSessionId.ToString())).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("ChatEditProposalsChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance);
        await notifier.EditProposalsChangedAsync(chatSessionId);

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.Single(capturedArgs!), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(chatSessionId, payload.GetProperty("chatSessionId").GetGuid());
        clients.Verify(c => c.Group(It.Is<string>(g => g != chatSessionId.ToString())), Times.Never);
        clients.Verify(c => c.All, Times.Never);
    }
}
