using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public class EventLogStore : IEventLogStore
{
    private readonly AppDbContext _db;

    public EventLogStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<int> AppendAsync(EventLog entry)
    {
        _db.EventLogs.Add(entry);
        await _db.SaveChangesAsync();
        return entry.Sequence;
    }

    public async Task<IReadOnlyList<EventLog>> GetByRunIdAsync(Guid runId)
        => await _db.EventLogs.Where(e => e.LoopRunId == runId).OrderBy(e => e.Sequence).ToListAsync();

    public async Task<IReadOnlyList<EventLog>> GetByRunIdLastNAsync(Guid runId, int n)
        => await _db.EventLogs
            .Where(e => e.LoopRunId == runId)
            .OrderByDescending(e => e.Sequence)
            .Take(n)
            .OrderBy(e => e.Sequence)
            .ToListAsync();

    public async Task<EventLog?> GetBySequenceAsync(Guid runId, int sequence)
        => await _db.EventLogs.FirstOrDefaultAsync(e => e.LoopRunId == runId && e.Sequence == sequence);

    public async Task<IReadOnlyList<EventLog>> GetOlderThanAsync(DateTimeOffset before)
        => await _db.EventLogs.Where(e => e.Timestamp < before.UtcDateTime).ToListAsync();

    public async Task RemoveRangeAsync(IReadOnlyList<EventLog> entries)
    {
        _db.EventLogs.RemoveRange(entries);
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<EventLog>> GetByRunIdAfterCursorAsync(Guid runId, int cursor, int limit)
        => await _db.EventLogs
            .Where(e => e.LoopRunId == runId && e.Sequence > cursor)
            .OrderBy(e => e.Sequence)
            .Take(limit)
            .ToListAsync();
}
