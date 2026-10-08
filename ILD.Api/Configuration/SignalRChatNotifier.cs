using ILD.Api.Hubs;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.DTOs.SignalRPayloads;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Configuration;

/// <summary>
/// Broadcasts chat turns into the per-session group on <see cref="ChatHub"/>, and
/// unread, title and activity hints into the owner's <see cref="ChatHub.InboxGroup">inbox group</see>.
/// Failures are swallowed and logged so a dropped notification never fails a turn.
/// </summary>
public class SignalRChatNotifier : IChatNotifier
{
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<SignalRChatNotifier> _log;

    public SignalRChatNotifier(IHubContext<ChatHub> hub, ILogger<SignalRChatNotifier> log)
    {
        _hub = hub;
        _log = log;
    }

    public Task MessageAppendedAsync(Guid chatSessionId, Guid turnId, ChatMessageView message)
        => SendAsync(chatSessionId, "ChatMessageAppended", new ChatMessageAppendedPayload(chatSessionId, turnId, message));

    public Task TurnProgressAsync(Guid chatSessionId, Guid turnId, string delta)
        => SendAsync(chatSessionId, "ChatTurnProgress", new ChatTurnProgressPayload(chatSessionId, turnId, delta));

    public Task TurnStartedAsync(Guid chatSessionId, Guid turnId)
        => SendAsync(chatSessionId, "ChatTurnStarted", new ChatTurnStartedPayload(chatSessionId, turnId));

    public Task TurnCompletedAsync(Guid chatSessionId, Guid turnId, bool interrupted)
        => SendAsync(chatSessionId, "ChatTurnCompleted", new ChatTurnCompletedPayload(chatSessionId, turnId, interrupted));

    public Task LoopUpdateRequestedAsync(Guid chatSessionId, string document)
        => SendAsync(chatSessionId, "ChatLoopUpdate", new ChatLoopUpdatePayload(chatSessionId, document));

    public Task EditProposalsChangedAsync(Guid chatSessionId)
        => SendAsync(chatSessionId, "ChatEditProposalsChanged", new ChatEditProposalsChangedPayload(chatSessionId));

    public Task UnreadChangedAsync(string userId, Guid chatSessionId)
        => SendAsync(ChatHub.InboxGroup(userId), chatSessionId, "ChatUnreadChanged", new ChatUnreadChangedPayload(chatSessionId));

    public Task TitleChangedAsync(string userId, Guid chatSessionId)
        => SendAsync(ChatHub.InboxGroup(userId), chatSessionId, "ChatTitleChanged", new ChatTitleChangedPayload(chatSessionId));

    public Task ActivityChangedAsync(string userId, Guid chatSessionId)
        => SendAsync(ChatHub.InboxGroup(userId), chatSessionId, "ChatActivityChanged", new ChatActivityChangedPayload(chatSessionId));

    private Task SendAsync(Guid chatSessionId, string eventName, object payload)
        => SendAsync(chatSessionId.ToString(), chatSessionId, eventName, payload);

    private async Task SendAsync(string group, Guid chatSessionId, string eventName, object payload)
    {
        try
        {
            await _hub.Clients.Group(group).SendAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to broadcast {Event} for chat {ChatSessionId}", eventName, chatSessionId);
        }
    }
}
