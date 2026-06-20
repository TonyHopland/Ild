using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Owns the lifecycle and single-turn execution of the standalone per-user Chat
/// Session (ADR-0010). Reuses the agent-adapter execution layer directly with a
/// synthesized <see cref="AgentExecutionContext"/> — no LoopRun involved.
/// </summary>
public interface IChatService
{
    /// <summary>The user's chat session with its rehydrated transcript, or null when none exists.</summary>
    Task<ChatSessionView?> GetForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Start the user's one chat session. Provider + tools are fixed for its life.
    /// Throws <see cref="InvalidOperationException"/> when a session already exists,
    /// the provider is unknown, or no adapter handles the provider type.
    /// </summary>
    Task<ChatSessionView> StartAsync(string userId, Guid aiProviderId, IReadOnlyList<string> tools, CancellationToken ct = default);

    /// <summary>
    /// Run one turn: append the user message, invoke the bound adapter session
    /// streaming progress over the chat notifier, then persist the assistant reply
    /// (flagged interrupted when <paramref name="ct"/> cancels mid-stream).
    /// </summary>
    Task ExecuteTurnAsync(Guid chatSessionId, string userMessage, CancellationToken ct);

    /// <summary>
    /// Hard-delete all chat-local state for the user — the session row, its adapter
    /// snapshots (cascade), its messages (cascade), and its scratch directory.
    /// Work items the chat created persist with their orphaned stamp.
    /// </summary>
    Task<bool> EndAsync(string userId, CancellationToken ct = default);

    /// <summary>Hard-delete chat sessions idle since before <paramref name="cutoff"/>. Returns the count removed.</summary>
    Task<int> SweepIdleAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
