using System.Security.Claims;
using ILD.Api.Hubs;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A chat group is named after the chat session id and nothing more, so joining
/// one is the whole of the authorization decision: a connection admitted to
/// another user's group receives that user's transcript for as long as it stays.
/// These pin that the hub authorizes the join the same way the REST surface
/// authorizes a per-chat action.
/// </summary>
public class ChatHubTests
{
    private static ChatHub BuildHub(string username, IChatService chat, out Mock<IGroupManager> groups)
    {
        groups = new Mock<IGroupManager>();
        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");
        context.SetupGet(c => c.User).Returns(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "ILD")));
        return new ChatHub(chat)
        {
            Groups = groups.Object,
            Context = context.Object,
        };
    }

    /// <summary>
    /// A chat service that only ever admits <paramref name="owner"/> to
    /// <paramref name="ownedSession"/> — the same predicate the real
    /// <c>ExistsForUserAsync</c> evaluates against the database.
    /// </summary>
    private static IChatService ChatOwnedBy(string owner, Guid ownedSession)
    {
        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExistsForUserAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string userId, Guid sessionId, CancellationToken _) =>
                userId == owner && sessionId == ownedSession);
        return chat.Object;
    }

    [Fact]
    public async Task SubscribeToChat_joins_the_group_for_the_chat_the_caller_owns()
    {
        var sessionId = Guid.NewGuid();
        var hub = BuildHub("alice", ChatOwnedBy("alice", sessionId), out var groups);

        await hub.SubscribeToChat(sessionId);

        groups.Verify(g => g.AddToGroupAsync("conn-1", sessionId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToChat_refuses_another_users_chat_without_joining_the_group()
    {
        var alicesSession = Guid.NewGuid();
        var hub = BuildHub("bob", ChatOwnedBy("alice", alicesSession), out var groups);

        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToChat(alicesSession));

        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeToChat_refuses_a_chat_that_does_not_exist()
    {
        // Indistinguishable from another user's chat by design: the refusal must
        // not tell the caller which of the two it hit.
        var hub = BuildHub("alice", ChatOwnedBy("alice", Guid.NewGuid()), out var groups);

        await Assert.ThrowsAsync<HubException>(() => hub.SubscribeToChat(Guid.NewGuid()));

        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UnsubscribeFromChat_leaves_the_group_unchecked()
    {
        // Unchecked by design: leaving a group grants nothing, and the cleanup of a
        // refused subscribe must still be able to run.
        var alicesSession = Guid.NewGuid();
        var hub = BuildHub("bob", ChatOwnedBy("alice", alicesSession), out var groups);

        await hub.UnsubscribeFromChat(alicesSession);

        groups.Verify(
            g => g.RemoveFromGroupAsync("conn-1", alicesSession.ToString(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
