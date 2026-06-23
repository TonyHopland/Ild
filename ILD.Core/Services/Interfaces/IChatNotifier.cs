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

    /// <summary>
    /// Push a full <c>ild-loop-template/v1</c> document to the open Loop Editor so
    /// it can validate and direct-apply it to the live canvas (loop editor context,
    /// ADR-0011). Fire-and-forget: the agent gets no structured ack — a rejected
    /// document is discovered only by re-reading the loop on a later turn.
    /// </summary>
    Task LoopUpdateRequestedAsync(Guid chatSessionId, string document);
}
