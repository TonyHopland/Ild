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
    /// </summary>
    Task SubmitAsync(Guid chatSessionId, string userMessage);

    /// <summary>Cancel any in-flight turn for the session and await its finalization.</summary>
    Task InterruptAsync(Guid chatSessionId);
}
