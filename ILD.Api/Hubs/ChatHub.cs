using ILD.Api.Authentication;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Hubs;

/// <summary>
/// Streams a Chat Session's turns to the bubble (ADR-0010). Clients join the
/// group named after their chat session id; the server broadcasts message and
/// progress events into that group from <see cref="Configuration.SignalRChatNotifier"/>.
///
/// A group name is a chat session id and nothing more, so joining is the whole
/// of the authorization decision: a connection that talks its way into another
/// user's group receives that user's transcript for as long as it stays. The
/// realtime path therefore authorizes exactly as the REST path does — see
/// <see cref="Controllers.ChatController"/>, which scopes every per-chat action
/// through <see cref="IChatService.ExistsForUserAsync"/> against the caller's
/// username.
///
/// <para>The attribute is deliberately redundant with the user-only fallback
/// policy that already covers this hub: it states at the class what the pipeline
/// states globally. It names <see cref="IldAuthentication.UserOnlyPolicy"/> and
/// not a bare <c>[Authorize]</c> for the reason documented there — a bare one
/// would suppress the fallback and leave the hub open to the agent token.</para>
/// </summary>
[Authorize(Policy = IldAuthentication.UserOnlyPolicy)]
public class ChatHub : Hub
{
    private readonly IChatService _chat;

    public ChatHub(IChatService chat)
    {
        _chat = chat;
    }

    /// <summary>
    /// Join the chat's group, but only when the caller owns the chat. Refusing
    /// with a <see cref="HubException"/> surfaces as a rejected invocation on the
    /// client rather than a silent no-op, and it says nothing about whether the
    /// session exists — the same posture as the controller's NotFound.
    /// </summary>
    public async Task SubscribeToChat(Guid chatSessionId)
    {
        // Agents never reach here: chats belong to the user-only surface, so the
        // fallback policy has already turned an agent token away before the
        // handshake. An authenticated user therefore always has a name.
        var userId = Context.User?.Identity?.Name;
        if (string.IsNullOrEmpty(userId))
            throw new HubException("Not authenticated.");

        // Authorize against the target chat (scoped by user) before joining its
        // group, so a connection can only ever receive turns from a chat the
        // caller owns.
        if (!await _chat.ExistsForUserAsync(userId, chatSessionId, Context.ConnectionAborted))
            throw new HubException("Chat not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, chatSessionId.ToString());
    }

    /// <summary>
    /// Leave the chat's group. Unchecked by design: leaving a group grants
    /// nothing, and a connection that should not have been in one is better off
    /// out of it — so a caller that cannot subscribe can still unsubscribe
    /// harmlessly, including the cleanup path of a subscribe that was refused.
    /// </summary>
    public async Task UnsubscribeFromChat(Guid chatSessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatSessionId.ToString());
    }
}
