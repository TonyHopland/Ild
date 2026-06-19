using Microsoft.AspNetCore.SignalR;

namespace ILD.Api.Hubs;

/// <summary>
/// Streams a Chat Session's turns to the bubble (ADR-0010). Clients join the
/// group named after their chat session id; the server broadcasts message and
/// progress events into that group from <see cref="Configuration.SignalRChatNotifier"/>.
/// </summary>
public class ChatHub : Hub
{
    public async Task SubscribeToChat(Guid chatSessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, chatSessionId.ToString());
    }

    public async Task UnsubscribeFromChat(Guid chatSessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatSessionId.ToString());
    }
}
