using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class EventLogService : IEventLogService
{
    private readonly IEventLogStore _eventLogStore;

    public EventLogService(IEventLogStore eventLogStore)
    {
        _eventLogStore = eventLogStore;
    }

    public Task<long> AppendAsync(Guid runId, EventType eventType, string message,
        Guid? nodeId = null, Guid? runNodeId = null, string? edgeName = null)
        => _eventLogStore.AppendAsync(Entry(runId, eventType, message, nodeId, runNodeId, edgeName));

    public Task<long> AppendAlongsideAsync(Guid runId, EventType eventType, string message,
        Guid? nodeId, Guid? runNodeId, string? edgeName, Func<Task> alongside)
        => _eventLogStore.AppendAlongsideAsync(Entry(runId, eventType, message, nodeId, runNodeId, edgeName), alongside);

    // Payloads are stored inline in the DB. PostgreSQL keeps the Data
    // column (text) out-of-line and LZ-compressed via TOAST once a value
    // exceeds a few KB, so large prompts/diffs cost nothing on the main row.
    private static EventLog Entry(Guid runId, EventType eventType, string message,
        Guid? nodeId, Guid? runNodeId, string? edgeName)
        => new()
        {
            LoopRunId = runId,
            EventType = eventType,
            NodeId = nodeId,
            RunNodeId = runNodeId,
            EdgeName = edgeName,
            Timestamp = DateTime.UtcNow,
            Data = message,
        };

    public Task<bool> HasRunEndedAsync(Guid runId) => _eventLogStore.HasEndedAsync(runId);

    public async Task<IEnumerable<EventLogEntry>> GetByRunIdAsync(Guid runId, int? limit = null)
    {
        var entries = await _eventLogStore.GetByRunIdAsync(runId);
        var list = entries.ToList();

        if (limit.HasValue)
            list = list.Take(limit.Value).ToList();

        return list.Select(e => new EventLogEntry(
            e.LoopRunId,
            e.EventType.ToString(),
            e.Data ?? string.Empty,
            e.RunNodeId,
            e.Timestamp));
    }

    public async Task<EventLogPage> GetByRunIdAfterCursorAsync(Guid runId, long cursor, int limit)
    {
        var entries = await _eventLogStore.GetByRunIdAfterCursorAsync(runId, cursor, limit);
        var list = entries.ToList();
        var hasMore = list.Count >= limit;
        var nextCursor = list.Count > 0 ? list[^1].Id : cursor;

        return new EventLogPage
        {
            Entries = list,
            NextCursor = nextCursor,
            HasMore = hasMore
        };
    }
}
