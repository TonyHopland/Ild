using System.Text.Json;
using ILD.Api.Configuration;
using ILD.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The hints every view re-reads Loop Template Proposals on, under the event
/// names the frontend listens for: the board's hub hears the template, a chat's
/// own group hears its chat.
/// </summary>
public class SignalRLoopTemplateProposalNotifierTests
{
    private static JsonElement Payload(object?[]? args)
        => JsonSerializer.SerializeToElement(Assert.Single(args!), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task The_work_item_hub_hears_LoopTemplateProposalsChanged_with_the_template_id()
    {
        var templateId = Guid.NewGuid();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group("work-items")).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<WorkItemHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);
        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("LoopTemplateProposalsChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        await new SignalRWorkItemNotifier(ctx.Object).LoopTemplateProposalsChangedAsync(templateId);

        Assert.Equal(templateId, Payload(capturedArgs).GetProperty("templateId").GetGuid());
    }

    [Fact]
    public async Task Only_that_chats_group_hears_ChatLoopTemplateProposalsChanged_with_its_id()
    {
        var chatSessionId = Guid.NewGuid();
        var clients = new Mock<IHubClients>();
        var proxy = new Mock<IClientProxy>();
        clients.Setup(c => c.Group(chatSessionId.ToString())).Returns(proxy.Object);
        var ctx = new Mock<IHubContext<ChatHub>>();
        ctx.SetupGet(c => c.Clients).Returns(clients.Object);
        object?[]? capturedArgs = null;
        proxy.Setup(p => p.SendCoreAsync("ChatLoopTemplateProposalsChanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => capturedArgs = args)
            .Returns(Task.CompletedTask);

        await new SignalRChatNotifier(ctx.Object, NullLogger<SignalRChatNotifier>.Instance).LoopTemplateProposalsChangedAsync(chatSessionId);

        Assert.Equal(chatSessionId, Payload(capturedArgs).GetProperty("chatSessionId").GetGuid());
        clients.Verify(c => c.Group(It.Is<string>(g => g != chatSessionId.ToString())), Times.Never);
        clients.Verify(c => c.All, Times.Never);
    }
}
