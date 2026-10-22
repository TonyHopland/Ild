using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Interfaces;

public static class EventLogServiceExtensions
{
    /// <summary>
    /// Record an event about a step the caller is taking anyway. Best-effort,
    /// as event writes are in the engine: a write that fails is logged and must
    /// not stop the step it describes. For a run-ending event, a
    /// <see cref="RunClosedException"/> means the end is already on record.
    /// A caller wired without an event log records nothing.
    /// </summary>
    public static async Task TryAppendAsync(
        this IEventLogService? events, Guid runId, EventType type, string message, ILogger logger)
    {
        if (events is null)
            return;
        try
        {
            await events.AppendAsync(runId, type, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record {EventType} on run {RunId}", type, runId);
        }
    }
}
