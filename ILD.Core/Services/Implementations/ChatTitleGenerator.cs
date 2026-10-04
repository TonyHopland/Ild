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

/// <summary>
/// Summarises a chat's first exchange into its title, when smart titles are on.
/// Runs on the provider the title tag resolves to — resolved exactly as an AI
/// node's tag is — as a side call that neither resumes nor records an agent
/// session, and saves the title only while the chat still carries its fallback,
/// so a rename always wins. One job per scope, run by <see cref="ChatTitleScheduler"/>,
/// which is also where anything this throws is logged.
/// </summary>
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

    public async Task GenerateAsync(Guid chatSessionId, string? openWorkItemId, CancellationToken ct)
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

        var firstExchange = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == chatSessionId && m.Sequence <= 1)
            .OrderBy(m => m.Sequence)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(ct);
        if (firstExchange is not [{ Role: "user" } first, { Role: "assistant" } reply])
        {
            _log.LogWarning("Chat {ChatSessionId} has no first exchange to title it from", chatSessionId);
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

        var adapter = _registry.ResolveForProvider(provider)();
        var prompt = ChatTitles.BuildPrompt(first.Content, reply.Content, await WorkItemTitleAsync(openWorkItemId, ct));
        var result = await adapter.ExecuteAsync(new AgentExecutionContext(
            provider,
            prompt,
            new LoopRunContext(
                // Chat turns run under the session id too, so whatever the adapter
                // keeps per run goes when the chat is deleted.
                LoopRunId: chatSessionId,
                WorkItemId: string.Empty,
                WorkItemTitle: string.Empty,
                WorkItemDescription: string.Empty,
                WorktreePath: session.ScratchPath,
                BranchName: string.Empty,
                EventLogSummary: new List<string>(),
                PreviousNodeOutput: null),
            ExecutionCount: 0,
            Cancel: ct,
            // Never null: that would hand the agent its default tools.
            ToolAllowlist: AiToolCatalog.NormalizeSelectedToolKeys(provider.Type, [AiToolCatalog.Read]),
            ManageSession: false));

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

    /// <summary>
    /// The title of the work item open when the chat started, as context only: a
    /// WorkItem server that is down or not configured costs the title nothing more.
    /// </summary>
    private async Task<string?> WorkItemTitleAsync(string? workItemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workItemId)) return null;
        try
        {
            return (await _workItems.GetWorkItemAsync(workItemId))?.Title;
        }
        catch (Exception ex) when (ChatService.IsWorkItemServerUnavailable(ex, ct))
        {
            _log.LogWarning(ex, "Could not read work item {WorkItemId} to title chat with", workItemId);
            return null;
        }
    }
}
