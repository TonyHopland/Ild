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

    [Fact]
    public async Task UnreadChangedAsync_hints_only_the_inbox_the_owners_connections_joined()
    {
        var chatSessionId = Guid.NewGuid();
        var alicesInbox = await ChatHubTests.InboxGroupJoinedBy("alice");
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(alicesInbox)).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("ChatUnreadChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance);
        await notifier.UnreadChangedAsync("alice", chatSessionId);

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.Single(capturedArgs!), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(chatSessionId, payload.GetProperty("chatSessionId").GetGuid());
        clients.Verify(c => c.Group(It.Is<string>(g => g != alicesInbox)), Times.Never);
        clients.Verify(c => c.Groups(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        clients.Verify(c => c.All, Times.Never);
        clients.Verify(c => c.User(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TitleChangedAsync_hints_only_the_inbox_the_owners_connections_joined()
    {
        var chatSessionId = Guid.NewGuid();
        var alicesInbox = await ChatHubTests.InboxGroupJoinedBy("alice");
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(alicesInbox)).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("ChatTitleChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance);
        await notifier.TitleChangedAsync("alice", chatSessionId);

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.Single(capturedArgs!), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(chatSessionId, payload.GetProperty("chatSessionId").GetGuid());
        clients.Verify(c => c.Group(It.Is<string>(g => g != alicesInbox)), Times.Never);
        clients.Verify(c => c.Groups(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        clients.Verify(c => c.All, Times.Never);
        clients.Verify(c => c.User(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ActivityChangedAsync_hints_only_the_inbox_the_owners_connections_joined()
    {
        var chatSessionId = Guid.NewGuid();
        var alicesInbox = await ChatHubTests.InboxGroupJoinedBy("alice");
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(alicesInbox)).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);

        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("ChatActivityChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        var notifier = new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance);
        await notifier.ActivityChangedAsync("alice", chatSessionId);

        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            Assert.Single(capturedArgs!), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(chatSessionId, payload.GetProperty("chatSessionId").GetGuid());
        clients.Verify(c => c.Group(It.Is<string>(g => g != alicesInbox)), Times.Never);
        clients.Verify(c => c.Groups(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        clients.Verify(c => c.All, Times.Never);
        clients.Verify(c => c.User(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ActivityChangedAsync_swallows_a_failed_broadcast()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);

        var notifier = new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance);

        await notifier.ActivityChangedAsync("alice", Guid.NewGuid());
        proxy.Verify(p => p.SendCoreAsync("ChatActivityChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnreadChangedAsync_swallows_a_failed_broadcast()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);

        var notifier = new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance);

        await notifier.UnreadChangedAsync("alice", Guid.NewGuid());
        proxy.Verify(p => p.SendCoreAsync("ChatUnreadChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
