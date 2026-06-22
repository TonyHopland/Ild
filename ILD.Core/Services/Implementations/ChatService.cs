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

    public ChatService(
        AppDbContext db,
        IProviderStore providers,
        IAgentAdapterRegistry registry,
        IChatNotifier notifier,
        ChatOptions options,
        ILoopRunStore runs)
    {
        _db = db;
        _providers = providers;
        _registry = registry;
        _notifier = notifier;
        _options = options;
        _runs = runs;
    }

    public async Task<ChatSessionView?> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        var session = await _db.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (session is null) return null;

        var messages = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == session.Id)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct);

        return ToView(session, messages);
    }

    public async Task<ChatSessionView> StartAsync(string userId, Guid aiProviderId, IReadOnlyList<string> tools, CancellationToken ct = default)
    {
        var existing = await _db.ChatSessions.AsNoTracking().AnyAsync(c => c.UserId == userId, ct);
        if (existing)
            throw new InvalidOperationException("A chat session already exists for this user. End it before starting a new one.");

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
        => ExecuteTurnAsync(chatSessionId, userMessage, openWorkItemId: null, ct);

    public async Task ExecuteTurnAsync(Guid chatSessionId, string userMessage, string? openWorkItemId, CancellationToken ct)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(c => c.Id == chatSessionId, ct);
        if (session is null) return;

        var nextSeq = await NextSequenceAsync(chatSessionId, ct);

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
            await BuildChatContextAsync(openWorkItemId, tools);
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
    /// Build the per-turn Chat Context (ADR-0011) for the open work item: a small
    /// preamble pushed into the model context, and the extra allowed directories
    /// granting access to the open item's active-run worktree. Returns
    /// <c>(null, null)</c> when no work item is open. The worktree path is granted
    /// only when BOTH a filesystem grant is held AND the open item has an active
    /// (non-terminal) run with a worktree on disk; otherwise the agent gets the
    /// id-only preamble and scratch access alone.
    /// </summary>
    private async Task<(string? Preamble, IReadOnlyList<string>? AllowedDirectories)> BuildChatContextAsync(
        string? openWorkItemId, IReadOnlyList<string> tools)
    {
        if (string.IsNullOrWhiteSpace(openWorkItemId))
            return (null, null);

        var lines = new List<string>
        {
            "[Chat Context]",
            $"The user currently has work item {openWorkItemId} open in the UI. Use the ILD tools "
                + "(e.g. get_workitem, the preview controls) with this work item id to inspect or act on it.",
        };

        IReadOnlyList<string>? allowedDirectories = null;

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
            var activeRun = await _runs.GetActiveByWorkItemAsync(openWorkItemId);
            var worktreePath = activeRun?.WorktreePath;
            if (!string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath))
            {
                lines.Add($"Its active run's worktree is checked out at: {worktreePath}");
                lines.Add("You may read and edit files there directly with your filesystem tools using that absolute path.");
                allowedDirectories = new[] { worktreePath };
            }
        }

        return (string.Join("\n", lines), allowedDirectories);
    }

    public async Task<bool> EndAsync(string userId, CancellationToken ct = default)
    {
        var session = await _db.ChatSessions.FirstOrDefaultAsync(c => c.UserId == userId, ct);
        if (session is null) return false;
        await DeleteSessionAsync(session, ct);
        return true;
    }

    public async Task<int> SweepIdleAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var cutoffUtc = cutoff.UtcDateTime;
        var stale = await _db.ChatSessions
            .Where(c => (c.UpdatedAt ?? c.CreatedAt) < cutoffUtc)
            .ToListAsync(ct);

        foreach (var session in stale)
        {
            if (ct.IsCancellationRequested) break;
            await DeleteSessionAsync(session, ct);
        }

        return stale.Count;
    }

    private async Task DeleteSessionAsync(ChatSession session, CancellationToken ct)
    {
        // Messages and adapter snapshots cascade-delete via their FKs.
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
            session.AiProviderId,
            session.ProviderType,
            session.ToolAllowlistCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            session.CreatedAt,
            messages.Select(ToView).ToList());
}
