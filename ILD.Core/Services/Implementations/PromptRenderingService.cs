using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public sealed class PromptRenderingService : IPromptRenderingService
{
    private readonly IPromptTemplateResolver _resolver;
    private readonly IEventLogStore _eventLog;
    private readonly IRunConversationService _conversation;
    private readonly ILoopRunStore _runs;
    private readonly ILogger<PromptRenderingService>? _logger;

    public PromptRenderingService(
        IPromptTemplateResolver resolver,
        IEventLogStore eventLog,
        IRunConversationService conversation,
        ILoopRunStore runs,
        ILogger<PromptRenderingService>? logger = null)
    {
        _resolver = resolver;
        _eventLog = eventLog;
        _conversation = conversation;
        _runs = runs;
        _logger = logger;
    }

    public async Task<string> RenderAsync(
        string? template,
        Guid runId,
        WorkItemView workItem,
        string? previousNodeOutput)
    {
        if (string.IsNullOrEmpty(template)) return "";

        // Read once: the summary and the conversation are two views of the same log.
        IReadOnlyList<string>? summary = null;
        IReadOnlyList<RunConversationMessage> messages = Array.Empty<RunConversationMessage>();
        try
        {
            var events = await _eventLog.GetByRunIdAsync(runId);
            summary = events.Select(e => $"{e.EventType}: {e.Data}").ToList();
            // The AI turns and human replies of the run's conversation; its system
            // messages (starts, parks, failures) are not part of these variables.
            messages = (await _conversation.ProjectAsync(runId, events))
                .Where(m => m.Role is RunConversationMessage.Ai or RunConversationMessage.Human)
                .ToList();
        }
        catch { /* the event log is best-effort */ }

        var conversationAi = string.Join("\n\n", messages
            .Where(m => m.Role == RunConversationMessage.Ai)
            .Select(Format));
        var conversationHuman = string.Join("\n\n", messages
            .Where(m => m.Role == RunConversationMessage.Human)
            .Select(m => m.Text));
        var conversationFull = string.Join("\n\n", messages.Select(Format));

        LogConversationSize(runId, conversationFull);

        IReadOnlyDictionary<string, string>? variables = null;
        try
        {
            var vars = await _runs.GetVariablesAsync(runId);
            if (vars.Count > 0)
                variables = vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* loop variables are best-effort, like the event log */ }

        return _resolver.Render(template, new PromptContext(
            WorkItemTitle: workItem.Title,
            WorkItemDescription: workItem.Description,
            PreviousNodeOutput: previousNodeOutput,
            EventLogSummary: summary,
            WorktreePath: workItem.WorktreePath,
            ConversationFull: conversationFull,
            ConversationAI: conversationAi,
            ConversationHuman: conversationHuman,
            RunVariables: variables));
    }

    // Attribution for the Full and AI views: author plus source node, stable and
    // readable. Human view is rendered verbatim (no prefix) so it can be treated
    // as an authoritative spec amendment free of any framing.
    private static string Format(RunConversationMessage m)
        => m.Role == RunConversationMessage.Ai
            ? $"[AI · {m.Name}] {m.Text}"
            : $"[Human] {m.Text}";

    // {{Conversation.Full}} grows unbounded with run length; measure the rendered
    // size so the eventual truncation/summarization follow-up has data to act on.
    private void LogConversationSize(Guid runId, string conversationFull)
    {
        if (conversationFull.Length == 0) return;
        _logger?.LogDebug(
            "Rendered {{Conversation.Full}} for run {RunId}: {CharCount} chars.",
            runId, conversationFull.Length);
    }
}
