using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>Titles a chat from its first message and a successful reply; saved only over the fallback, so a rename always wins.</summary>
public sealed class ChatTitleGenerator
{
    private readonly AppDbContext _db;
    private readonly IProviderStore _providers;
    private readonly IAgentAdapterRegistry _registry;
    private readonly IAppSettingStore _settings;
    private readonly IChatNotifier _notifier;
    private readonly IWorkItemManager _workItems;
    private readonly ILogger<ChatTitleGenerator> _log;

    public ChatTitleGenerator(
        AppDbContext db,
        IProviderStore providers,
        IAgentAdapterRegistry registry,
        IAppSettingStore settings,
        IChatNotifier notifier,
        IWorkItemManager workItems,
        ILogger<ChatTitleGenerator> log)
    {
        _db = db;
        _providers = providers;
        _registry = registry;
        _settings = settings;
        _notifier = notifier;
        _workItems = workItems;
        _log = log;
    }

    public async Task GenerateAsync(Guid chatSessionId, string? openWorkItemId, int replySequence, CancellationToken ct)
    {
        // Read when the job runs rather than when the turn handed it off, so turning
        // the switch either way applies to the next first exchange.
        var smartTitles = await _settings.GetByKeyAsync(AppSettingKeys.ChatSmartTitles, ct);
        if (!string.Equals(smartTitles?.Value, "true", StringComparison.OrdinalIgnoreCase)) return;

        var session = await _db.ChatSessions.AsNoTracking()
            .Where(c => c.Id == chatSessionId)
            .Select(c => new { c.UserId, c.TitleSource, c.ScratchPath })
            .FirstOrDefaultAsync(ct);
        if (session is null)
        {
            _log.LogWarning("Chat {ChatSessionId} was deleted before its title was generated", chatSessionId);
            return;
        }
        if (session.TitleSource != ChatTitleSource.Fallback) return;

        var first = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == chatSessionId && m.Sequence == 0 && m.Role == "user")
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct);
        var reply = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == chatSessionId && m.Sequence == replySequence && m.Role == "assistant")
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct);
        if (first is null || reply is null)
        {
            _log.LogWarning("Chat {ChatSessionId} has no exchange to title it from", chatSessionId);
            return;
        }

        var tag = (await _settings.GetByKeyAsync(AppSettingKeys.ChatTitleProviderTag, ct))?.Value;
        var (provider, providerError) = await AiNodeProviderResolver.ResolveAsync(
            _providers, tag, RemoteAiProviderOverrideMode.None, overrideId: null);
        if (provider is null)
        {
            _log.LogWarning("Chat {ChatSessionId} keeps its title: {Error}", chatSessionId, providerError);
            return;
        }
        if (!AiToolCatalog.SupportsNoTools(provider.Type))
        {
            _log.LogWarning(
                "Chat {ChatSessionId} keeps its title: provider {Provider} ({ProviderType}) cannot run a call without tools",
                chatSessionId, provider.Name, provider.Type);
            return;
        }

        var adapter = _registry.ResolveForProvider(provider)();
        var prompt = ChatTitles.BuildPrompt(first, reply, await WorkItemTitleAsync(openWorkItemId, ct));
        var result = await AskAsync(adapter, provider, prompt, session.ScratchPath, ct);

        if (!result.Success)
        {
            _log.LogWarning("The title model for chat {ChatSessionId} failed on provider {Provider}: {Error}",
                chatSessionId, provider.Name, result.Error);
            return;
        }
        if (ChatTitles.CleanGenerated(result.Output ?? string.Empty) is not { } title)
        {
            _log.LogWarning("The title model for chat {ChatSessionId} answered with no usable title", chatSessionId);
            return;
        }

        var saved = await _db.ChatSessions
            .Where(c => c.Id == chatSessionId && c.TitleSource == ChatTitleSource.Fallback)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Name, title)
                .SetProperty(c => c.TitleSource, ChatTitleSource.Auto), ct) > 0;
        if (saved)
            await _notifier.TitleChangedAsync(session.UserId, chatSessionId);
        else if (!await _db.ChatSessions.AsNoTracking().AnyAsync(c => c.Id == chatSessionId, ct))
            _log.LogWarning("Chat {ChatSessionId} was deleted while its title was generated", chatSessionId);
    }

    // A run id of its own: the chat's next turn may run meanwhile, and adapters keep
    // per-run files (pi's provider config) that two providers must not share.
    private async Task<NodeExecutionResult> AskAsync(
        IAgentAdapter adapter, AiProvider provider, string prompt, string scratchPath, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        try
        {
            return await adapter.ExecuteAsync(new AgentExecutionContext(
                provider,
                prompt,
                new LoopRunContext(
                    LoopRunId: runId,
                    WorkItemId: string.Empty,
                    WorkItemTitle: string.Empty,
                    WorkItemDescription: string.Empty,
                    WorktreePath: scratchPath,
                    BranchName: string.Empty,
                    EventLogSummary: new List<string>(),
                    PreviousNodeOutput: null),
                ExecutionCount: 0,
                Cancel: ct,
                // The adapters ignore this under NoTools: the call gets no tools at all.
                ToolAllowlist: AiToolCatalog.NormalizeSelectedToolKeys(provider.Type, [AiToolCatalog.Read]),
                ManageSession: false,
                NoTools: true));
        }
        finally
        {
            try
            {
                if (!await AgentRunFiles.DeleteAsync(runId, CancellationToken.None))
                    _log.LogWarning("Could not remove everything the title model left under run {RunId}", runId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not remove the files of title run {RunId}", runId);
            }
        }
    }

    // Context only: a work item that cannot be read costs the title nothing more.
    private async Task<string?> WorkItemTitleAsync(string? workItemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workItemId)) return null;
        try
        {
            return (await _workItems.GetWorkItemAsync(workItemId).WaitAsync(ct))?.Title;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Could not read work item {WorkItemId} to title chat with", workItemId);
            return null;
        }
    }
}
