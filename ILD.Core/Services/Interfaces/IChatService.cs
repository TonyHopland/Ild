using ILD.Core.Services.Attachments;
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

    /// <summary>
    /// Start a new chat session for the user. Provider + tools are fixed for its
    /// life. A user may hold many retained chats (ADR-0013), so this no longer
    /// rejects a second session. Throws <see cref="InvalidOperationException"/>
    /// when the provider is unknown or no adapter handles the provider type.
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

    /// <inheritdoc cref="ExecuteTurnAsync(Guid, string, string?, string?, CancellationToken)"/>
    /// <param name="attachmentIds">
    /// Attachments already stored by <see cref="SaveAttachmentsAsync"/>. The turn
    /// links them to the transcript entry it creates, writes them into the
    /// session's scratch directory for the agent to open, and removes those files
    /// once the turn is over.
    /// </param>
    Task ExecuteTurnAsync(Guid chatSessionId, string userMessage, string? openWorkItemId, string? openLoopDocument, IReadOnlyList<Guid>? attachmentIds, CancellationToken ct);

    /// <summary>
    /// Store files uploaded with a chat message and return their ids, to hand to
    /// <see cref="ExecuteTurnAsync(Guid, string, string?, string?, IReadOnlyList{Guid}?, CancellationToken)"/>.
    ///
    /// <para>
    /// The bytes go into the database, not onto disk: a chat's scratch directory
    /// is readable by the agent uid, and one uid serves every chat, so anything
    /// left there for the life of a chat is readable by every later agent. The
    /// database is out of that uid's reach entirely.
    /// </para>
    ///
    /// Scoped by <paramref name="userId"/>; returns null when the chat does not
    /// exist or belongs to another user. Throws
    /// <see cref="AttachmentRejectedException"/> for an upload that exceeds the
    /// size or count limits.
    /// </summary>
    Task<IReadOnlyList<AttachmentView>?> SaveAttachmentsAsync(
        string userId, Guid sessionId, IReadOnlyList<UploadedFile> files, CancellationToken ct = default);

    /// <summary>
    /// One attachment of one of the user's chats, by id, with its bytes, so the
    /// transcript can offer it back for download. Null when the chat is not the
    /// user's or the id is unknown. The caller owns the stream.
    ///
    /// <para>
    /// Read from the database, so showing an attachment to a human never touches
    /// the filesystem. That is what removes the confused deputy this used to be:
    /// there is no path to validate, and nothing the agent could swap underneath
    /// it between the check and the read.
    /// </para>
    /// </summary>
    Task<(AttachmentView Meta, Stream Content)?> OpenAttachmentAsync(
        string userId, Guid sessionId, Guid attachmentId, CancellationToken ct = default);

    /// <summary>
    /// Hard-delete one of the user's chats — the session row, its adapter snapshots
    /// (cascade), its messages (cascade), and its scratch directory. Scoped by
    /// <paramref name="userId"/>; returns false when the chat does not exist or
    /// belongs to another user. Work items the chat created persist with their
    /// orphaned stamp.
    /// </summary>
    Task<bool> DeleteAsync(string userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Hard-delete every chat the user owns (the "delete all" action). Returns the
    /// count removed.
    /// </summary>
    Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default);
}
