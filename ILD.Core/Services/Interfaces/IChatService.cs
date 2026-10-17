using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Owns the lifecycle and single-turn execution of standalone Chat Sessions
/// (ADR-0010). Reuses the agent-adapter execution layer directly with a
/// synthesized <see cref="AgentExecutionContext"/> — no LoopRun involved. A user
/// retains many chats as browsable history (ADR-0013).
/// </summary>
public interface IChatService
{
    /// <summary>
    /// The user's retained chats as lightweight history rows (no transcripts),
    /// newest activity first.
    /// </summary>
    Task<IReadOnlyList<ChatSessionSummaryView>> ListForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// One of the user's chats with its rehydrated transcript, scoped by
    /// <paramref name="userId"/> for authorization. Null when the chat does not
    /// exist or belongs to another user.
    /// </summary>
    Task<ChatSessionView?> GetByIdAsync(string userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Whether the chat exists and belongs to <paramref name="userId"/>. A
    /// lightweight ownership check (no transcript load) for authorizing per-chat
    /// actions such as sending a message or deleting.
    /// </summary>
    Task<bool> ExistsForUserAsync(string userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>The user the chat belongs to, or null when it does not exist (or has been deleted).</summary>
    Task<string?> GetOwnerAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Record that the user has read their chat up to <paramref name="sequence"/>.
    /// The marker only ever rises, so a sequence at or below the stored one changes
    /// nothing. Returns whether it moved, and false when the chat does not exist or
    /// belongs to another user. A raise hints the owner's inbox.
    /// </summary>
    Task<bool> MarkReadAsync(string userId, Guid sessionId, int sequence, CancellationToken ct = default);

    /// <summary>Renames one of the user's chats for good; false when it is not theirs or does not exist.</summary>
    Task<bool> RenameAsync(string userId, Guid sessionId, string name, CancellationToken ct = default);

    /// <summary>
    /// The ids of the user's chats with a message containing
    /// <paramref name="query"/> case-insensitively, newest activity first.
    /// </summary>
    Task<IReadOnlyList<Guid>> SearchForUserAsync(string userId, string query, CancellationToken ct = default);

    /// <summary>
    /// Stars or unstars one of the user's chats without touching its last activity;
    /// false when it is not theirs or does not exist.
    /// </summary>
    Task<bool> SetFavoriteAsync(string userId, Guid sessionId, bool favorite, CancellationToken ct = default);

    /// <summary>
    /// Start a new chat session for the user. Provider + tools are fixed for its
    /// life. A user may hold many retained chats (ADR-0013), so this no longer
    /// rejects a second session. Throws <see cref="InvalidOperationException"/>
    /// when the provider is unknown or no adapter handles the provider type.
    /// Null <paramref name="tools"/> means the provider's default tools.
    /// </summary>
    Task<ChatSessionView> StartAsync(string userId, Guid aiProviderId, IReadOnlyList<string>? tools, CancellationToken ct = default);

    /// <summary>
    /// Run one turn: append the user message, invoke the bound adapter session
    /// streaming progress over the chat notifier, then persist the assistant reply
    /// (flagged interrupted when <paramref name="ct"/> cancels mid-stream).
    /// <paramref name="turnId"/> names this run of the agent: it travels with every
    /// message and delta the turn produces, so a client can tell them from those of
    /// a turn that has since replaced it.
    /// </summary>
    Task ExecuteTurnAsync(Guid chatSessionId, Guid turnId, string userMessage, CancellationToken ct);

    /// <summary>
    /// Run one turn with the ambient per-turn Chat Context (ADR-0011):
    /// <paramref name="openWorkItemId"/> is the work item the user currently has
    /// open, pushed into the model context as a thin pointer and used to grant the
    /// item's active-run worktree as an extra allowed directory (gated by the
    /// session's filesystem tools). <paramref name="openLoopDocument"/> is the live
    /// <c>ild-loop-template/v2</c> document of the loop open in the Loop Editor (or
    /// null when none is open); it is stashed in the per-session loop scratchpad,
    /// overwritten every message, and only a "loop editor is open" flag enters the
    /// model context — the agent pulls the JSON on demand via <c>get_current_loop</c>.
    /// A null/empty work item and document run a context-free turn.
    /// </summary>
    Task ExecuteTurnAsync(Guid chatSessionId, Guid turnId, string userMessage, string? openWorkItemId, string? openLoopDocument, CancellationToken ct);

    /// <summary>
    /// Hard-delete one of the user's chats — the session row, its adapter snapshots
    /// (cascade), its messages (cascade), and its scratch directory. Scoped by
    /// <paramref name="userId"/>; returns false when the chat does not exist or
    /// belongs to another user. Work items the chat created persist with their
    /// orphaned stamp. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the chat's pi ILD extension
    /// (which holds the API token) cannot be removed; the chat is then kept so the
    /// delete can be retried.
    /// </summary>
    Task<bool> DeleteAsync(string userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Every user's unstarred chats whose last activity — the newest message of
    /// either role, or with none the chat's creation — is before
    /// <paramref name="cutoff"/>. Renaming, starring or reading a chat is not activity.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListInactiveAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>
    /// Hard-delete the chat as <see cref="DeleteAsync"/> does, whoever owns it, if it
    /// is still unstarred and inactive since before <paramref name="cutoff"/> by the
    /// rule of <see cref="ListInactiveAsync"/>, read afresh. Returns whether it was
    /// deleted, and throws like <see cref="DeleteAsync"/>. Not scoped to a user: for
    /// the inactivity sweep only, which holds the chat's turn gate around it so no
    /// turn can add a message between the check and the delete.
    /// </summary>
    Task<bool> DeleteIfInactiveAsync(Guid sessionId, DateTime cutoff, CancellationToken ct = default);
}
