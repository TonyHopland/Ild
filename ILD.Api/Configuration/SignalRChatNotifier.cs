using ILD.Api.Hubs;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.DTOs.SignalRPayloads;
using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Configuration;

/// <summary>
/// Broadcasts chat turns into the per-session group on <see cref="ChatHub"/>.
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

    public Task MessageAppendedAsync(Guid chatSessionId, ChatMessageView message)
        => SendAsync(chatSessionId, "ChatMessageAppended", new ChatMessageAppendedPayload(chatSessionId, message));

    public Task TurnProgressAsync(Guid chatSessionId, string delta)
        => SendAsync(chatSessionId, "ChatTurnProgress", new ChatTurnProgressPayload(chatSessionId, delta));

    public Task TurnCompletedAsync(Guid chatSessionId, bool interrupted)
        => SendAsync(chatSessionId, "ChatTurnCompleted", new ChatTurnCompletedPayload(chatSessionId, interrupted));

    private async Task SendAsync(Guid chatSessionId, string eventName, object payload)
    {
        try
        {
            await _hub.Clients.Group(chatSessionId.ToString()).SendAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to broadcast {Event} for chat {ChatSessionId}", eventName, chatSessionId);
        }
    }
}
