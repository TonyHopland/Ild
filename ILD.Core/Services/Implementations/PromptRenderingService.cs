using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public sealed class PromptRenderingService : IPromptRenderingService
{
    private readonly IPromptTemplateResolver _resolver;
    private readonly IEventLogService _eventLog;
    private readonly ILoopRunStore _runs;
    private readonly ILogger<PromptRenderingService>? _logger;

    public PromptRenderingService(
        IPromptTemplateResolver resolver,
        IEventLogService eventLog,
        ILoopRunStore runs,
        ILogger<PromptRenderingService>? logger = null)
    {
        _resolver = resolver;
        _eventLog = eventLog;
        _runs = runs;
        _logger = logger;
    }

    // One run-scoped message in the assembled conversation. AI messages come
    // from AI-node outputs; Human messages from HumanFeedbackReceived events.
    private enum Author { Ai, Human }

    private sealed record Message(Author Author, DateTime Timestamp, string Source, string Text);

    public async Task<string> RenderAsync(
        string? template,
        Guid runId,
        WorkItemView workItem,
        string? previousNodeOutput)
    {
        if (string.IsNullOrEmpty(template)) return "";

        IReadOnlyList<string>? summary = null;
        var aiMessages = new List<Message>();
        var humanMessages = new List<Message>();
        try
        {
            var entries = await _eventLog.GetByRunIdAsync(runId);
            var entryList = entries.ToList();
            summary = entryList.Select(e => $"{e.EventType}: {e.Data}").ToList();

            humanMessages.AddRange(entryList
                .Where(e => e.EventType == EventType.HumanFeedbackReceived.ToString()
                            && !string.IsNullOrEmpty(e.Data))
                .Select(e => new Message(Author.Human, e.Timestamp, "Human", e.Data)));
        }
        catch { /* event log is best-effort */ }

        try
        {
            var runNodes = await _runs.GetRunNodesWithNodeAsync(runId);
            aiMessages.AddRange(runNodes
                .Where(rn => rn.LoopNode?.NodeType == NodeType.AI
                             && !string.IsNullOrEmpty(rn.Output))
                .Select(rn => new Message(
                    Author.Ai,
                    rn.CompletedAt ?? rn.CreatedAt,
                    rn.NodeLabel ?? rn.LoopNode?.Label ?? "AI",
                    rn.Output!)));
        }
        catch { /* run-node history is best-effort */ }

        var conversationAi = string.Join("\n\n", aiMessages.Select(Format));
        var conversationHuman = string.Join("\n\n", humanMessages.Select(m => m.Text));
        var conversationFull = string.Join("\n\n", aiMessages
            .Concat(humanMessages)
            .OrderBy(m => m.Timestamp)
            .Select(Format));

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
    private static string Format(Message m)
        => m.Author == Author.Ai
            ? $"[AI · {m.Source}] {m.Text}"
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
