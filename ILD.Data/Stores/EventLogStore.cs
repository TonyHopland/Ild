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

    public async Task<long> AppendAsync(EventLog entry)
    {
        if (entry.LoopRunId is not { } runId)
            throw new ArgumentException("An event must name the run it belongs to.", nameof(entry));

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Lock the run row for the rest of the transaction. Appends to one run
            // then commit one at a time, in the order they took their Ids, so a
            // reader paging by Id never passes an Id that commits later; and the
            // closed check below cannot race the run-ending event it looks for.
            var runs = await _db.LoopRuns
                .Where(r => r.Id == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, r => r.Status));
            if (runs == 0)
                throw new InvalidOperationException($"Run {runId} not found while appending an event.");

            if (await IsConversationAsync(entry) && await HasEndedAsync(runId))
                throw new RunClosedException(runId);

            _db.EventLogs.Add(entry);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return entry.Id;
        }
        catch
        {
            // The context may be shared and long-lived; a row it failed to write
            // must not ride along with the next unrelated save.
            _db.Entry(entry).State = EntityState.Detached;
            throw;
        }
    }

    private async Task<bool> IsConversationAsync(EventLog entry)
    {
        var fromAiNode = entry.EventType == EventType.NodeCompleted
            && entry.RunNodeId is { } runNodeId
            && await _db.LoopRunNodes.AnyAsync(rn => rn.Id == runNodeId && rn.LoopNode.NodeType == NodeType.AI);
        return RunConversationEvents.Contains(entry.EventType, fromAiNode);
    }

    public Task<bool> HasEndedAsync(Guid runId)
        => _db.EventLogs.AnyAsync(e => e.LoopRunId == runId
            && (e.EventType == EventType.LoopRunCompleted
                || e.EventType == EventType.LoopRunFailed
                || e.EventType == EventType.LoopRunCancelled));

    public async Task<IReadOnlyList<EventLog>> GetByRunIdAsync(Guid runId)
        => await _db.EventLogs.AsNoTracking().Where(e => e.LoopRunId == runId).OrderBy(e => e.Id).ToListAsync();

    public async Task<IReadOnlyList<EventLog>> GetByRunIdLastNAsync(Guid runId, int n)
        => await _db.EventLogs
            .Where(e => e.LoopRunId == runId)
            .OrderByDescending(e => e.Id)
            .Take(n)
            .OrderBy(e => e.Id)
            .ToListAsync();

    public async Task<IReadOnlyList<EventLog>> GetByRunIdAfterCursorAsync(Guid runId, long cursor, int limit)
        => await _db.EventLogs
            .Where(e => e.LoopRunId == runId && e.Id > cursor)
            .OrderBy(e => e.Id)
            .Take(limit)
            .ToListAsync();
}
