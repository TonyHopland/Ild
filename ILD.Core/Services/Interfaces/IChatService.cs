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
    /// Run one turn with the ambient per-turn Chat Context (ADR-0011):
    /// <paramref name="openWorkItemId"/> is the work item the user currently has
    /// open, pushed into the model context as a thin pointer and used to grant the
    /// item's active-run worktree as an extra allowed directory (gated by the
    /// session's filesystem tools). <paramref name="openLoopDocument"/> is the live
    /// <c>ild-loop-template/v1</c> document of the loop open in the Loop Editor (or
    /// null when none is open); it is stashed in the per-session loop scratchpad,
    /// overwritten every message, and only a "loop editor is open" flag enters the
    /// model context — the agent pulls the JSON on demand via <c>get_current_loop</c>.
    /// A null/empty work item and document run a context-free turn.
    /// </summary>
    Task ExecuteTurnAsync(Guid chatSessionId, string userMessage, string? openWorkItemId, string? openLoopDocument, CancellationToken ct);

    /// <summary>
    /// Hard-delete all chat-local state for the user — the session row, its adapter
    /// snapshots (cascade), its messages (cascade), and its scratch directory.
    /// Work items the chat created persist with their orphaned stamp.
    /// </summary>
    Task<bool> EndAsync(string userId, CancellationToken ct = default);

    /// <summary>Hard-delete chat sessions idle since before <paramref name="cutoff"/>. Returns the count removed.</summary>
    Task<int> SweepIdleAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
