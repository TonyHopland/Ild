using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Streams chat turns to the <c>/hubs/chat</c> SignalR hub. All methods are
/// best-effort — a notification failure never fails a turn.
/// </summary>
public interface IChatNotifier
{
    /// <summary>A finalized transcript message (user or assistant) was appended.</summary>
    Task MessageAppendedAsync(Guid chatSessionId, ChatMessageView message);

    /// <summary>A streamed delta of the in-flight assistant reply.</summary>
    Task TurnProgressAsync(Guid chatSessionId, string delta);

    /// <summary>The current turn finished (or was interrupted), so the live bubble can settle.</summary>
    Task TurnCompletedAsync(Guid chatSessionId, bool interrupted);
}
