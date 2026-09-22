using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Streams chat turns to the <c>/hubs/chat</c> SignalR hub. All methods are
/// best-effort — a notification failure never fails a turn.
/// </summary>
public interface IChatNotifier
{
    /// <summary>
    /// A finalized transcript message (user or assistant) was appended by the turn
    /// named by <paramref name="turnId"/>. Every message belongs to a turn, and a
    /// turn that has since been replaced still finalizes its own: the id is what
    /// keeps that late reply from disturbing what the live turn is streaming.
    /// </summary>
    Task MessageAppendedAsync(Guid chatSessionId, Guid turnId, ChatMessageView message);

    /// <summary>A streamed delta of the reply the turn named by <paramref name="turnId"/> is writing.</summary>
    Task TurnProgressAsync(Guid chatSessionId, Guid turnId, string delta);

    /// <summary>
    /// A turn started for the session. Announced by whoever starts it, before any
    /// turn it replaces is cancelled, so the bubble never sees a busy chat go quiet
    /// during a hand-over.
    /// </summary>
    Task TurnStartedAsync(Guid chatSessionId, Guid turnId);

    /// <summary>
    /// The turn named by <paramref name="turnId"/> finished (or was interrupted),
    /// so the live bubble can settle — but only if that is still the turn it is
    /// watching, which is why the id travels with it.
    /// </summary>
    Task TurnCompletedAsync(Guid chatSessionId, Guid turnId, bool interrupted);

    /// <summary>
    /// Push a full <c>ild-loop-template/v1</c> document to the open Loop Editor so
    /// it can validate and direct-apply it to the live canvas (loop editor context,
    /// ADR-0011). Fire-and-forget: the agent gets no structured ack — a rejected
    /// document is discovered only by re-reading the loop on a later turn.
    /// </summary>
    Task LoopUpdateRequestedAsync(Guid chatSessionId, string document);
}
