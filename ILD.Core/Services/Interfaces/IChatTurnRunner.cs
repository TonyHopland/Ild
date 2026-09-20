namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Serializes turn execution for a chat session and implements the interrupt
/// primitive: submitting a new message while a turn is streaming cancels the
/// in-flight adapter (keeping its partial reply flagged interrupted) and then
/// starts a fresh turn that resumes the same bound session. Messages interrupt;
/// they never queue.
/// </summary>
public interface IChatTurnRunner
{
    /// <summary>
    /// Interrupt any in-flight turn for the session, then start a new background
    /// turn for <paramref name="userMessage"/>. Returns once the new turn has
    /// started (it streams to completion in the background).
    /// <paramref name="openWorkItemId"/> and <paramref name="openLoopDocument"/> are
    /// the ambient per-turn Chat Context (ADR-0011): the work item the user has open
    /// and the live loop document of the open Loop Editor, or null when neither is
    /// open.
    /// </summary>
    Task SubmitAsync(Guid chatSessionId, string userMessage, string? openWorkItemId = null, string? openLoopDocument = null);

    /// <summary>Cancel any in-flight turn for the session and await its finalization.</summary>
    Task InterruptAsync(Guid chatSessionId);

    /// <summary>
    /// The turn currently in flight for the session, or null when it has none.
    /// The runner is the only thing that knows: a client that has just loaded or
    /// just reconnected has no other way to learn the chat is mid-turn.
    /// </summary>
    Guid? ActiveTurnId(Guid chatSessionId);

    /// <summary>
    /// Cancel any in-flight turn for the session, then run <paramref name="delete"/>
    /// while still holding the session's turn gate, so a message submitted in the
    /// meantime cannot start a turn until the session is gone. That turn then finds
    /// no session and does nothing, so it cannot recreate what the delete removed.
    /// </summary>
    Task DeleteAsync(Guid chatSessionId, Func<Task> delete);
}
