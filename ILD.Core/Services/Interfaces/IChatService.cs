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
    /// <param name="attachments">
    /// Files already stored by <see cref="SaveAttachmentsAsync"/>. Their absolute
    /// paths are appended to the prompt so the agent can open them, and the
    /// metadata is kept on the persisted user turn so reopening the chat still
    /// shows what was attached.
    /// </param>
    Task ExecuteTurnAsync(Guid chatSessionId, string userMessage, string? openWorkItemId, string? openLoopDocument, IReadOnlyList<AttachmentRef>? attachments, CancellationToken ct);

    /// <summary>
    /// Store files uploaded with a chat message in the session's scratch
    /// directory — the agent's own working directory — and return what to hand
    /// to <see cref="ExecuteTurnAsync(Guid, string, string?, string?, IReadOnlyList{AttachmentRef}?, CancellationToken)"/>.
    /// Scoped by <paramref name="userId"/>; returns null when the chat does not
    /// exist or belongs to another user. Throws
    /// <see cref="AttachmentRejectedException"/> for an upload that exceeds the
    /// size or count limits.
    /// </summary>
    Task<IReadOnlyList<AttachmentRef>?> SaveAttachmentsAsync(
        string userId, Guid sessionId, IReadOnlyList<UploadedFile> files, CancellationToken ct = default);

    /// <summary>
    /// One attachment of one of the user's chats, by id, with an open handle on
    /// its bytes so the transcript can offer it back for download. Null when the
    /// chat is not the user's, the id is unknown, or the file is not one this
    /// service is willing to serve. The caller owns the stream.
    ///
    /// <para>
    /// Deliberately not "return a path the caller re-opens": these files live in
    /// a tree the agent can reach, and it can swap a directory between the
    /// validation and a second open by name — which would leave the checks
    /// describing a file other than the one served. A handle opened while the
    /// path is known good is pinned to that file.
    /// </para>
    /// </summary>
    Task<(AttachmentRef Meta, Stream Content)?> OpenAttachmentAsync(
        string userId, Guid sessionId, string attachmentId, CancellationToken ct = default);

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
