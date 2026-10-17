namespace ILD.Core.Services.Implementations;

/// <summary>
/// Configuration for the standalone chat feature: where per-session scratch
/// directories live. Chats are retained until the user deletes them (ADR-0013),
/// or, if the opt-in setting is on, <see cref="ChatInactivitySweeper"/> finds them inactive.
/// </summary>
public sealed class ChatOptions
{
    /// <summary>Root directory under which each chat session's scratch dir is created.</summary>
    public string ScratchRoot { get; init; } = Path.Combine("data", "chat-sessions");
}
