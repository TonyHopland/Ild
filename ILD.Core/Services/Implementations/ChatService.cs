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

        // Persist the human's verbatim message; the Chat Context preamble is an
        // ambient per-turn hint for the model only, never part of the transcript.
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
        var (contextPreamble, additionalAllowedDirectories) =
            await BuildChatContextAsync(openWorkItemId, !string.IsNullOrWhiteSpace(openLoopDocument), tools);
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

        var newSessionId = result.SessionId ?? capturedSessionId ?? session.CurrentSessionId;
        await FinalizeAssistantAsync(session, nextSeq + 1, content, interrupted, newSessionId, ct);
    }

    /// <summary>
    /// Build the per-turn Chat Context (ADR-0011): a small preamble pushed into the
    /// model context, and the extra allowed directories granting access to the open
    /// work item's active-run worktree. Returns <c>(null, null)</c> when nothing is
    /// open. The worktree path is granted only when BOTH a filesystem grant is held
    /// AND the open item has an active (non-terminal) run with a worktree on disk;
    /// otherwise the agent gets the id-only preamble and scratch access alone. When
    /// <paramref name="loopEditorOpen"/> is set, the preamble names the open Loop
    /// Editor as a thin pointer — the agent reads/edits the loop via the
    /// <c>get_current_loop</c>/<c>update_current_loop</c> tools.
    /// </summary>
    private async Task<(string? Preamble, IReadOnlyList<string>? AllowedDirectories)> BuildChatContextAsync(
        string? openWorkItemId, bool loopEditorOpen, IReadOnlyList<string> tools)
    {
        var hasWorkItem = !string.IsNullOrWhiteSpace(openWorkItemId);
        if (!hasWorkItem && !loopEditorOpen)
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

        if (loopEditorOpen)
        {
            lines.Add(
                "The user has a loop open in the Loop Editor. Call get_current_loop to read it as the "
                + "ild-loop-template/v1 document. To EDIT it, prefer the targeted tools — they change only "
                + "what you name (never corrupting an unrelated node) and each returns a synchronous ack "
                + "{ applied, matchCount, validationErrors }: use get_loop_node + edit_loop_node_field for a "
                + "prompt/config tweak (plain-text find-and-replace, the server handles JSON escaping; "
                + "old_string must match exactly once), set_loop_node_field to overwrite a whole field, and "
                + "edit_loop_file for structural nudges (edges, ids). update_current_loop (full replacement) "
                + "is a last resort. Every edit applies to the live canvas immediately but is transient — only "
                + "the human can save.");
            lines.Add(LoopAuthoringGuide);
        }

        return (string.Join("\n", lines), allowedDirectories);
    }

    /// <summary>
    /// A compact primer on the loop template model so the chat agent can author a
    /// valid <c>ild-loop-template/v1</c> document — included in the Chat Context only
    /// when a Loop Editor is open (ADR-0011). Mirrors the node/edge vocabulary in
    /// CONTEXT.md and the config fields the editor reads/writes.
    /// </summary>
    private const string LoopAuthoringGuide =
        """
        Loop authoring guide — a loop is a directed graph executed from its Start node.
        Document shape: { "$schema": "ild-loop-template/v1", "name", "description", "recoveryPolicy" (AutoResume|NeedsReview|Cancel), "nodes": [...], "edges": [...] }.
        Each node: { "id", "type", "label" (unique), "config": {...} }. Each edge: { "id", "sourceNodeId", "targetNodeId", "edgeType" (OnSuccess|OnFailure|Custom), "name" (Custom only) }.
        Node types and their key config fields:
        - Start: entry point; creates the worktree/branch. config.createWorktree (bool), config.runInstall (bool).
        - Cmd: runs a shell command in the worktree, succeeds on exit 0. config.command.
        - AI: runs the agent. config.prompt, config.aiProviderId, config.toolAllowlist (string[]), config.matchRules ([{ "pattern", "edgeName" }] routing the AI output to Custom edges by name; no match takes OnSuccess).
        - Human: pauses for human input (becomes {{PreviousNode.Output}}). config.inputLabel, config.prompt, config.customEdges (string[] of Custom edge names this node may emit).
        - Prompt: renders a templated string as its Output (compose a downstream AI prompt). config.prompt. Always routes OnSuccess.
        - PR: opens/maintains a pull request. config.prDescriptionTemplate, config.prCommentTemplate, config.customEdges. Reserved PR edges: on_rejected, on_merge_conflict, on_ci_failed, on_approved, on_ci_passed, on_merged, on_abandoned — wire on_merged/on_abandoned to reach a terminal path.
        - Condition: a switch. config.cases ([{ "variant" (TextMatches|PrExists|HasTag), "subject"+"pattern" for TextMatches, "tag" for HasTag, "edgeName" }]), config.defaultEdge (the Custom edge taken when no case matches), config.output (the pass-through).
        - Cleanup: terminal sink (incoming edges only); marks the run finished.
        Field semantics you cannot infer from the name:
        - id: yours to choose, and only internal consistency matters — saving mints fresh GUIDs and remaps every reference. An edge whose sourceNodeId or targetNodeId names no node in the same document is silently dropped, with no error and no validation failure. After any structural edit, re-read the document and confirm each edge you added is still there.
        - aiProviderId: omit it unless a GUID was handed to you — you have no way to list providers. Unset or non-GUID falls back to the default provider; a GUID that no longer exists fails the run outright.
        - toolAllowlist: exactly four keys exist — "read", "write", "execute", "ild" — and only opencode/pi/claude-code providers honour them. Unknown keys are filtered out, and an empty, omitted or fully-filtered list means the PROVIDER DEFAULTS, not "no tools"; you cannot express "no tools" here.
        - Condition subject and output: template strings rendered through the placeholder pipeline before matching, both defaulting to {{Node.Input}}.
        - Precedence, and they differ: for AI matchRules the rule matching LAST in the output wins, so a closing verdict beats a word mentioned earlier; for Condition cases the FIRST matching case wins. Both are case-insensitive singleline regexes, and invalid, zero-width (x*, \b, (?=...)) or catastrophically slow patterns are rejected at save.
        Graph rules enforced when the human saves — a rejected save costs a whole round trip, so check them before you hand back:
        - A Start node and a Cleanup node must exist, and at least one path must run Start → Cleanup.
        - EVERY node must be reachable from Start. This is the rule a hand-written document fails most often: a node you added but never wired in rejects the entire save, not just itself.
        - Per source node: at most one OnSuccess and one OnFailure edge. Custom edges only on Human/AI/PR/Condition, each with a non-empty name unique within that node. Cleanup takes no outgoing edges.
        - A Condition needs ≥1 case and a defaultEdge, must have NO OnSuccess edge, and its case/default edge names must match its wired Custom edges exactly, both ways.
        Edges: give every non-terminal node an OnSuccess edge. Use OnFailure edges sparingly — only when you genuinely want a distinct recovery path (e.g. an AI fix/retry loop). Most failures are transient (an AI node out of tokens or a throttled provider, a flaky command); a node that fails with NO OnFailure edge fails in place and parks the run for human feedback, so a human can fix and restart that node. That is almost always better than wiring OnFailure to Cleanup, which discards the run on a hiccup — do not route failures to Cleanup.
        Variables: templated fields expand placeholders — {{WorkItem.Title}}/{{WorkItem.Description}}, {{PreviousNode.Output}} (also spelled {{Node.Input}}), {{EventLog.LastN}}, and {{Var.<name>}}. A field is expanded exactly once, by the node that owns it; text a placeholder pulls in is never scanned again, so a prompt, a description or a variable value may quote the grammar safely. A Loop Variable is a mutable per-run string an AI node writes (via the agent variable API/tools) for a later node to read; names match [A-Za-z][A-Za-z0-9_]*.
        Sessions: config.useSession=true is what turns session handling on; without it the other two fields are ignored and every visit starts fresh. With it, config.sessionPlaceholder="<name>" resumes the session bound to that name (use it when the node continues its own earlier work), and config.forkFromPlaceholder="<name>" re-copies another placeholder's session on every visit (use it when a branch needs that context without writing back into it). Prefer a fresh session for work that does not depend on an earlier conversation — it is cheaper and cannot inherit a stale plan.
        Authoring practices:
        - Keep an AI node's config.prompt down to a single placeholder, e.g. {{PreviousNode.Output}}, and put the full brief in an upstream Prompt node. The AI node re-renders and re-sends config.prompt on EVERY visit, so a brief inlined there is paid again on each retry pass around the loop.
        - Anchor matchRules on a verdict you instruct the agent to emit last (^TO_REVIEW$ rather than TO_REVIEW): last match wins, so an anchored final line cannot be beaten by the same word discussed earlier in the output.
        - Point config.defaultEdge somewhere useful — a terminal path or a human gate. Every output your cases did not anticipate lands there, so a token branch silently swallows the interesting cases.
        - Hand work across branches with {{Var.<name>}}, not {{PreviousNode.Output}}. Output is positional — only the node that just ran — while a loop variable is run-scoped and readable from any later node, whichever branch wrote it.
        """;

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
