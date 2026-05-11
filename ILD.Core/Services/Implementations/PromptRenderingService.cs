using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations;

public sealed class PromptRenderingService : IPromptRenderingService
{
    private readonly IPromptTemplateResolver _resolver;
    private readonly IEventLogService _eventLog;

    public PromptRenderingService(IPromptTemplateResolver resolver, IEventLogService eventLog)
    {
        _resolver = resolver;
        _eventLog = eventLog;
    }

    public async Task<string> RenderAsync(
        string? template,
        Guid runId,
        WorkItemView workItem,
        string? previousNodeOutput)
    {
        if (string.IsNullOrEmpty(template)) return "";

        IReadOnlyList<string>? summary = null;
        try
        {
            var entries = await _eventLog.GetByRunIdAsync(runId);
            summary = entries.Select(e => $"{e.EventType}: {e.Data}").ToList();
        }
        catch { /* event log is best-effort */ }

        return _resolver.Render(template, new PromptContext(
            WorkItemTitle: workItem.Title,
            WorkItemDescription: workItem.Description,
            PreviousNodeOutput: previousNodeOutput,
            EventLogSummary: summary,
            WorktreePath: workItem.WorktreePath));
    }
}
