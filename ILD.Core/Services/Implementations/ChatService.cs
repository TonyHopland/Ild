using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Standalone chat orchestrator (ADR-0010). The thin turn wrapper in
/// <see cref="ExecuteTurnAsync"/> replaces the graph plumbing of the AI node
/// executor: it synthesizes an <see cref="AgentExecutionContext"/> over the
/// session's scratch directory and drives the bound adapter session directly.
///
/// Context-aware chat (ADR-0011): each turn carries an ambient, per-turn Chat
/// Context — the open work item id, and (when it has an active run and the
/// session holds a filesystem grant) that run's worktree path. The id is pushed
/// into the model context via a small prompt preamble; the worktree is reached
/// by absolute path through an extra allowed directory, never by relocating the
/// agent's working directory off its durable scratch dir.
///
/// A turn resumes the provider's agent session rather than replaying our own
/// transcript, so the preamble is only ambient from ILD's side — every copy it
/// sends stays in the agent's history. Anything constant therefore goes in once
/// per session (<see cref="ChatSession.LoopGuideSessionId"/>) and is pullable
/// afterwards via <c>ild_get_loop_authoring_guide</c>; only genuinely per-turn
/// state rides the prompt every turn.
/// </summary>
public sealed class ChatService : IChatService
{
    private readonly AppDbContext _db;
    private readonly IProviderStore _providers;
    private readonly IAgentAdapterRegistry _registry;
    private readonly IChatNotifier _notifier;
    private readonly ChatOptions _options;
    private readonly ILoopRunStore _runs;
    private readonly IChatLoopScratchpad _loopScratchpad;

    public ChatService(
        AppDbContext db,
        IProviderStore providers,
        IAgentAdapterRegistry registry,
        IChatNotifier notifier,
        ChatOptions options,
        ILoopRunStore runs,
        IChatLoopScratchpad loopScratchpad)
    {
        _db = db;
        _providers = providers;
        _registry = registry;
        _notifier = notifier;
        _options = options;
        _runs = runs;
        _loopScratchpad = loopScratchpad;
    }

    public async Task<IReadOnlyList<ChatSessionSummaryView>> ListForUserAsync(string userId, CancellationToken ct = default)
    {
        // Lightweight history rows only — no transcripts. Newest activity first so
        // the most recently used chats top the list.
        return await _db.ChatSessions.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt)
            .Select(c => new ChatSessionSummaryView(c.Id, c.Name, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<ChatSessionView?> GetByIdAsync(string userId, Guid sessionId, CancellationToken ct = default)
    {
        // Scope by userId as well as id so one user can never resume another's chat.
        var session = await _db.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == sessionId && c.UserId == userId, ct);
        if (session is null) return null;

        var messages = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == session.Id)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

        return ToView(session, messages);
    }

    public Task<bool> ExistsForUserAsync(string userId, Guid sessionId, CancellationToken ct = default)
        => _db.ChatSessions.AsNoTracking().AnyAsync(c => c.Id == sessionId && c.UserId == userId, ct);

    public async Task<ChatSessionView> StartAsync(string userId, Guid aiProviderId, IReadOnlyList<string> tools, CancellationToken ct = default)
    {
        var provider = await _providers.GetAiProviderByIdAsync(aiProviderId)
            ?? throw new InvalidOperationException($"AiProvider {aiProviderId} not found");

        // Fail fast if no adapter handles the provider type.
        _ = _registry.ResolveForProvider(provider);

        var normalizedTools = AiToolCatalog.NormalizeSelectedToolKeys(provider.Type, tools);

        var id = Guid.NewGuid();
        var scratchPath = Path.GetFullPath(Path.Combine(_options.ScratchRoot, id.ToString("N")));
        Directory.CreateDirectory(scratchPath);

        var session = new ChatSession
        {
            Id = id,
            UserId = userId,
            AiProviderId = provider.Id,
            ProviderType = provider.Type,
            ToolAllowlistCsv = string.Join(',', normalizedTools),
            ScratchPath = scratchPath,
        };
        _db.ChatSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        return ToView(session, Array.Empty<ChatMessage>());
    }

    public Task ExecuteTurnAsync(Guid chatSessionId, string userMessage, CancellationToken ct)
        => ExecuteTurnAsync(chatSessionId, userMessage, openWorkItemId: null, openLoopDocument: null, ct);

    public async Task ExecuteTurnAsync(Guid chatSessionId, string userMessage, string? openWorkItemId, string? openLoopDocument, CancellationToken ct)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(c => c.Id == chatSessionId, ct);
        if (session is null) return;

        // Stash the live loop document for this turn, overwriting any prior entry
        // (a null/empty document clears it). The agent reads it back through the
        // get_current_loop tool — only the "loop editor is open" flag below enters
        // the model context unprompted.
        _loopScratchpad.Set(chatSessionId, openLoopDocument);

        var nextSeq = await NextSequenceAsync(chatSessionId, ct);

        // Name the chat from its first user message (ADR-0013) so the history list
        // shows something meaningful without asking the user to type a title. The
        // session is tracked, so the name persists with the turn's SaveChanges.
        if (nextSeq == 0 && string.IsNullOrEmpty(session.Name))
            session.Name = DeriveName(userMessage);

        // Persist the human's verbatim message only — the Chat Context preamble is
        // never part of OUR transcript. It is not transient for the model, though:
        // a turn resumes the provider's session, so whatever the preamble carries
        // stays in the agent's history for the rest of that session. That is why the
        // static half is delivered once per session and not per turn (#27).
        var userEntry = await AppendMessageAsync(chatSessionId, "user", userMessage, interrupted: false, nextSeq, ct);
        await _notifier.MessageAppendedAsync(chatSessionId, ToView(userEntry));

        var provider = await _providers.GetAiProviderByIdAsync(session.AiProviderId);
        if (provider is null)
        {
            await FinalizeAssistantAsync(session, nextSeq + 1,
                $"[chat-error] AI provider {session.AiProviderId} is no longer configured.", interrupted: false, newSessionId: null, ct);
            return;
        }

        IAgentAdapter adapter;
        try
        {
            adapter = _registry.ResolveForProvider(provider)();
        }
        catch (Exception ex)
        {
            await FinalizeAssistantAsync(session, nextSeq + 1,
                $"[chat-error] no adapter for provider type '{provider.Type}': {ex.Message}", interrupted: false, newSessionId: null, ct);
            return;
        }

        var tools = session.ToolAllowlistCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Resolve the ambient per-turn Chat Context (ADR-0011): a preamble naming
        // the open work item that is pushed into the model context, plus the
        // active-run worktree path granted as an extra allowed directory when the
        // session also holds a filesystem grant.
        //
        // The loop authoring guide is the one part of the preamble that is neither
        // per-turn nor small, so it is delivered once per agent session instead of
        // once per turn (#27): a turn resumes the provider's session rather than
        // replaying our own transcript, so a copy sent on an earlier turn is still
        // in front of the agent. Keyed on the session that holds it, so a session
        // that is rebound or forked is briefed again rather than left without it.
        var loopEditor =
            string.IsNullOrWhiteSpace(openLoopDocument) ? LoopEditorContext.Closed
            : session.LoopGuideSessionId is not null && session.LoopGuideSessionId == session.CurrentSessionId
                ? LoopEditorContext.Briefed
                : LoopEditorContext.NeedsBriefing;

        var (contextPreamble, additionalAllowedDirectories) =
            await BuildChatContextAsync(openWorkItemId, loopEditor, tools);
        var promptForAgent = contextPreamble is null
            ? userMessage
            : $"{contextPreamble}\n\n{userMessage}";

        var runContext = new LoopRunContext(
            LoopRunId: session.Id,
            WorkItemId: string.Empty,
            WorkItemTitle: string.Empty,
            WorkItemDescription: string.Empty,
            WorktreePath: session.ScratchPath,
            BranchName: string.Empty,
            EventLogSummary: new List<string>(),
            PreviousNodeOutput: null);

        var streamed = new System.Text.StringBuilder();
        string? capturedSessionId = null;

        var agentCtx = new AgentExecutionContext(
            provider,
            promptForAgent,
            runContext,
            ExecutionCount: 0,
            Cancel: ct,
            ProgressCallback: async chunk =>
            {
                streamed.Append(chunk);
                await _notifier.TurnProgressAsync(chatSessionId, chunk);
            },
            AdapterConfig: null,
            ToolAllowlist: tools,
            SessionId: session.CurrentSessionId,
            IncomingSessionId: session.CurrentSessionId,
            ManageSession: true,
            OnSessionId: sid => capturedSessionId = sid,
            ForkFromSessionId: null,
            ChatSessionId: session.Id,
            AdditionalAllowedDirectories: additionalAllowedDirectories);

        NodeExecutionResult result;
        try
        {
            result = await adapter.ExecuteAsync(agentCtx);
        }
        catch (OperationCanceledException)
        {
            result = NodeExecutionResult.Fail("interrupted");
        }
        catch (Exception ex)
        {
            result = NodeExecutionResult.Fail($"[chat-error] {ex.Message}");
        }

        var interrupted = ct.IsCancellationRequested;
        string content;
        if (interrupted)
            content = streamed.ToString();
        else if (result.Success)
            content = string.IsNullOrEmpty(result.Output) ? streamed.ToString() : result.Output!;
        else
            content = !string.IsNullOrWhiteSpace(result.Output) ? result.Output! : $"[chat-error] {result.Error}";

        // This turn's OWN binding, which is not the same thing as the session the
        // turn ends on: newSessionId below falls back to the binding we were already
        // resuming, so a turn that never reached an agent still ends on one. Every
        // adapter failure path returns no session id (the claude launch failing, the
        // process throwing before it started, ExecuteAsync throwing above), so a null
        // here is precisely "the prompt did not land".
        var boundThisTurn = result.SessionId ?? capturedSessionId;
        var newSessionId = boundThisTurn ?? session.CurrentSessionId;

        // Only a turn that reached the agent can have left the guide with it. Using
        // newSessionId here instead would record a briefing that never arrived
        // whenever the agent failed to launch on an already-bound session, and no
        // later turn would ever brief that session again. Erring this way costs one
        // redundant copy; erring the other way is silent and permanent.
        if (loopEditor == LoopEditorContext.NeedsBriefing && boundThisTurn is not null)
            session.LoopGuideSessionId = boundThisTurn;

        await FinalizeAssistantAsync(session, nextSeq + 1, content, interrupted, newSessionId, ct);
    }

    /// <summary>
    /// Build the per-turn Chat Context (ADR-0011): a small preamble pushed into the
    /// model context, and the extra allowed directories granting access to the open
    /// work item's active-run worktree. Returns <c>(null, null)</c> when nothing is
    /// open. The worktree path is granted only when BOTH a filesystem grant is held
    /// AND the open item has an active (non-terminal) run with a worktree on disk;
    /// otherwise the agent gets the id-only preamble and scratch access alone. When
    /// <paramref name="loopEditor"/> is open, the preamble names it as a thin pointer
    /// — the agent reads/edits the loop via the
    /// <c>get_current_loop</c>/<c>update_current_loop</c> tools — and, on the turn
    /// that first briefs an agent session, carries the constant brief and authoring
    /// guide as well. Everything else here is per-turn state by nature and is
    /// rebuilt on each call.
    /// </summary>
    private async Task<(string? Preamble, IReadOnlyList<string>? AllowedDirectories)> BuildChatContextAsync(
        string? openWorkItemId, LoopEditorContext loopEditor, IReadOnlyList<string> tools)
    {
        var hasWorkItem = !string.IsNullOrWhiteSpace(openWorkItemId);
        if (!hasWorkItem && loopEditor == LoopEditorContext.Closed)
            return (null, null);

        var lines = new List<string> { "[Chat Context]" };
        IReadOnlyList<string>? allowedDirectories = null;

        if (hasWorkItem)
        {
            lines.Add(
                $"The user currently has work item {openWorkItemId} open in the UI. Use the ILD tools "
                + "(e.g. get_workitem, the preview controls) with this work item id to inspect or act on it.");

            // Filesystem access must be granted on the session before exposing any
            // worktree path — without a read/write/execute tool the directory grant
            // would be inert anyway.
            var hasFilesystemGrant = tools.Any(t =>
                string.Equals(t, AiToolCatalog.Read, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, AiToolCatalog.Write, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t, AiToolCatalog.Execute, StringComparison.OrdinalIgnoreCase));

            if (hasFilesystemGrant)
            {
                // Active run only (ADR-0011): finished-run worktrees are kept on disk
                // per ADR-0008 but are not exposed to the chat.
                var activeRun = await _runs.GetActiveByWorkItemAsync(openWorkItemId!);
                var worktreePath = activeRun?.WorktreePath;
                if (!string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath))
                {
                    lines.Add($"Its active run's worktree is checked out at: {worktreePath}");
                    lines.Add("You may read and edit files there directly with your filesystem tools using that absolute path.");
                    allowedDirectories = new[] { worktreePath };
                }
            }
        }

        switch (loopEditor)
        {
            case LoopEditorContext.NeedsBriefing:
                lines.Add(LoopEditorBrief);
                lines.Add(LoopAuthoringGuide.Text);
                break;
            case LoopEditorContext.Briefed:
                lines.Add(LoopEditorReminder);
                break;
        }

        return (string.Join("\n", lines), allowedDirectories);
    }

    /// <summary>
    /// What the Loop Editor half of the Chat Context owes a turn. The two underlying
    /// facts — whether an editor is open, and whether this agent session already
    /// holds the guide — are not independent: "brief now" is meaningless with no
    /// editor open. One value makes that combination unrepresentable rather than
    /// merely unused.
    /// </summary>
    private enum LoopEditorContext
    {
        /// <summary>No Loop Editor open: the loop half of the preamble is skipped.</summary>
        Closed,

        /// <summary>Open, and this agent session already holds the guide: pointer only.</summary>
        Briefed,

        /// <summary>Open, and this agent session does not hold the guide yet: full brief.</summary>
        NeedsBriefing,
    }

    /// <summary>
    /// The full loop-editor brief, delivered on the turn that first opens the Loop
    /// Editor in an agent session and immediately followed by
    /// <see cref="LoopAuthoringGuide.Text"/>. Both are constant for the session's
    /// life, so they are pushed once into the resumed agent session rather than
    /// re-sent per turn; <see cref="LoopEditorReminder"/> carries the per-turn state
    /// afterwards.
    /// </summary>
    private const string LoopEditorBrief =
        "The user has a loop open in the Loop Editor. Call get_current_loop to read it as the "
        + "ild-loop-template/v1 document. To EDIT it, prefer the targeted tools — they change only "
        + "what you name (never corrupting an unrelated node) and each returns a synchronous ack "
        + "{ applied, matchCount, validationErrors }: use get_loop_node + edit_loop_node_field for a "
        + "prompt/config tweak (plain-text find-and-replace, the server handles JSON escaping; "
        + "old_string must match exactly once), set_loop_node_field to overwrite a whole field, and "
        + "edit_loop_file for structural nudges (edges, ids). update_current_loop (full replacement) "
        + "is a last resort. Every edit applies to the live canvas immediately but is transient — only "
        + "the human can save. The authoring guide below is sent once for this session; call "
        + "get_loop_authoring_guide to read it again at any point.";

    /// <summary>
    /// The standing per-turn line for a Loop Editor that is open but has already been
    /// briefed. Whether an editor is open at all is per-turn state, so something must
    /// travel every turn — this is the thin pointer ADR-0011 asks for, and it names
    /// the tool that pulls the guide back, so dropping the guide from the per-turn
    /// channel does not put it out of reach.
    /// </summary>
    private const string LoopEditorReminder =
        "The user has a loop open in the Loop Editor. Read it with get_current_loop and change it "
        + "with the targeted loop tools; edits reach the live canvas immediately but are transient — "
        + "only the human can save. Call get_loop_authoring_guide for the loop model, the field "
        + "semantics and the save-time graph rules.";

    public async Task<bool> DeleteAsync(string userId, Guid sessionId, CancellationToken ct = default)
    {
        // Scope by userId so a delete can only ever touch the caller's own chat.
        var session = await _db.ChatSessions.FirstOrDefaultAsync(c => c.Id == sessionId && c.UserId == userId, ct);
        if (session is null) return false;
        await DeleteSessionAsync(session, ct);
        return true;
    }

    public async Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default)
    {
        var sessions = await _db.ChatSessions.Where(c => c.UserId == userId).ToListAsync(ct);
        foreach (var session in sessions)
        {
            if (ct.IsCancellationRequested) break;
            await DeleteSessionAsync(session, ct);
        }
        return sessions.Count;
    }

    /// <summary>
    /// Derive a short, meaningful chat name from the first user message (ADR-0013):
    /// collapse whitespace and truncate to a sensible length, appending an ellipsis
    /// when trimmed. Falls back to a generic label for an empty/whitespace message.
    /// </summary>
    private static string DeriveName(string firstMessage)
    {
        const int maxLength = 60;
        var collapsed = string.Join(' ', firstMessage.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0) return "New chat";
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength].TrimEnd() + "…";
    }

    private async Task DeleteSessionAsync(ChatSession session, CancellationToken ct)
    {
        // Messages and adapter snapshots cascade-delete via their FKs; the loop
        // scratchpad is in-memory only, so drop its entry explicitly.
        _loopScratchpad.Clear(session.Id);
        _db.ChatSessions.Remove(session);
        await _db.SaveChangesAsync(ct);

        // Best-effort scratch-dir removal: nothing chat-local should remain, but a
        // leftover directory must never fail the hard-delete.
        try
        {
            if (!string.IsNullOrEmpty(session.ScratchPath) && Directory.Exists(session.ScratchPath))
                Directory.Delete(session.ScratchPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task FinalizeAssistantAsync(
        ChatSession session, int sequence, string content, bool interrupted, string? newSessionId, CancellationToken ct)
    {
        var assistant = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = session.Id,
            Role = "assistant",
            Content = content,
            Interrupted = interrupted,
            Sequence = sequence,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ChatMessages.Add(assistant);

        session.CurrentSessionId = newSessionId;
        session.UpdatedAt = DateTime.UtcNow;

        // Persist transcript even on interrupt: cancellation is the expected path,
        // not an error, so honor it with CancellationToken.None.
        await _db.SaveChangesAsync(CancellationToken.None);

        await _notifier.MessageAppendedAsync(session.Id, ToView(assistant));
        await _notifier.TurnCompletedAsync(session.Id, interrupted);
    }

    private async Task<ChatMessage> AppendMessageAsync(
        Guid chatSessionId, string role, string content, bool interrupted, int sequence, CancellationToken ct)
    {
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatSessionId,
            Role = role,
            Content = content,
            Interrupted = interrupted,
            Sequence = sequence,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ChatMessages.Add(message);
        await _db.SaveChangesAsync(ct);
        return message;
    }

    private async Task<int> NextSequenceAsync(Guid chatSessionId, CancellationToken ct)
    {
        var hasAny = await _db.ChatMessages.AnyAsync(m => m.ChatSessionId == chatSessionId, ct);
        if (!hasAny) return 0;
        var max = await _db.ChatMessages
            .Where(m => m.ChatSessionId == chatSessionId)
            .MaxAsync(m => m.Sequence, ct);
        return max + 1;
    }

    private static ChatMessageView ToView(ChatMessage m)
        => new(m.Id, m.Role, m.Content, m.Interrupted, m.Sequence, m.CreatedAt);

    private static ChatSessionView ToView(ChatSession session, IReadOnlyList<ChatMessage> messages)
        => new(
            session.Id,
            session.Name,
            session.AiProviderId,
            session.ProviderType,
            session.ToolAllowlistCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            session.CreatedAt,
            messages.Select(ToView).ToList());
}
