namespace ILD.Core.Services.Implementations;

/// <summary>
/// Configuration for the standalone chat feature: where per-session scratch
/// directories live. Chats are retained until the user deletes them (ADR-0013) —
/// there is no inactivity sweeper.
/// </summary>
public sealed class ChatOptions
{
    /// <summary>Root directory under which each chat session's scratch dir is created.</summary>
    public string ScratchRoot { get; init; } = Path.Combine("data", "chat-sessions");
}
