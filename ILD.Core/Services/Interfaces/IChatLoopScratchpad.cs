namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Per-session scratchpad holding the live <c>ild-loop-template/v1</c> document
/// the user has open in the Loop Editor (loop editor context, ADR-0011). The
/// browser includes the live document on each <c>sendMessage</c>; the server
/// overwrites the entry every message so the agent always reads the loop as-of
/// the current turn — there is no continuous mirror and no staleness window. The
/// JSON enters the model context only when the agent pulls it via the
/// <c>get_current_loop</c> tool.
/// </summary>
public interface IChatLoopScratchpad
{
    /// <summary>
    /// Stash the live loop document for a chat session, replacing any previous
    /// entry. A null/empty document clears the entry — the Loop Editor is closed
    /// (or was never open) this turn, so the agent must see no loop.
    /// </summary>
    void Set(Guid chatSessionId, string? document);

    /// <summary>The session's stashed loop document, or null when none is open.</summary>
    string? Get(Guid chatSessionId);

    /// <summary>Drop the session's entry — called when the chat session is deleted.</summary>
    void Clear(Guid chatSessionId);
}
