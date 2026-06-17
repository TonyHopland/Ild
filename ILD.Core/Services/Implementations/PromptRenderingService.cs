using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class PromptRenderingService : IPromptRenderingService
{
    private readonly IPromptTemplateResolver _resolver;
    private readonly IEventLogService _eventLog;
    private readonly ILoopRunStore _runs;

    public PromptRenderingService(IPromptTemplateResolver resolver, IEventLogService eventLog, ILoopRunStore runs)
    {
        _resolver = resolver;
        _eventLog = eventLog;
        _runs = runs;
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
            RunVariables: variables));
    }
}
