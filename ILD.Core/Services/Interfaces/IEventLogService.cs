using ILD.Data.DTOs;
using ILD.Data.Enums;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The only writer of the event log, every run's single ordered timeline.
/// </summary>
public interface IEventLogService
{
    /// <summary>
    /// Append an event to the run <paramref name="runId"/> names, and return its
    /// Id. Throws <see cref="ILD.Data.Stores.RunClosedException"/> for a
    /// conversation event on a run that has ended.
    /// </summary>
    Task<long> AppendAsync(Guid runId, EventType eventType, string message,
        Guid? nodeId = null, Guid? runNodeId = null, string? edgeName = null);

    /// <summary>Whether the run's run-ending event has been written.</summary>
    Task<bool> HasRunEndedAsync(Guid runId);

    Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null);
    Task<EventLogPage> GetByRunIdAfterCursorAsync(Guid runId, long cursor, int limit);
}
