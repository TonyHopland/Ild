using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface IEventLogStore
{
    Task<int> AppendAsync(EventLog entry);
    Task<IReadOnlyList<EventLog>> GetByRunIdAsync(Guid runId);
    Task<IReadOnlyList<EventLog>> GetByRunIdLastNAsync(Guid runId, int n);
    Task<EventLog?> GetBySequenceAsync(Guid runId, int sequence);
    Task<IReadOnlyList<EventLog>> GetOlderThanAsync(DateTimeOffset before);
    Task RemoveRangeAsync(IReadOnlyList<EventLog> entries);
    Task<IReadOnlyList<EventLog>> GetByRunIdAfterCursorAsync(Guid runId, int cursor, int limit);
}
