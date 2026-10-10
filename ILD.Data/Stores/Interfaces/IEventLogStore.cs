using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface IEventLogStore
{
    /// <summary>
    /// Insert <paramref name="entry"/> on its run and return its Id. Appends to
    /// one run commit one at a time in Id order. A conversation event (see
    /// <see cref="Enums.RunConversationEvents"/>) on a run whose run-ending event
    /// already exists throws <see cref="RunClosedException"/> and writes nothing;
    /// the check and the insert are one transaction under the run's row lock.
    /// </summary>
    Task<long> AppendAsync(EventLog entry);

    /// <summary>
    /// <see cref="AppendAsync"/>, running <paramref name="alongside"/> inside the
    /// same transaction after the run is locked and found open, before the insert.
    /// Its writes and the event commit together or not at all: if it throws,
    /// nothing is written and the exception propagates. It must use this
    /// store's context and not open a transaction of its own.
    /// </summary>
    Task<long> AppendAlongsideAsync(EventLog entry, Func<Task> alongside);

    /// <summary>Whether the run's run-ending event (completed, failed, cancelled) has been written.</summary>
    Task<bool> HasEndedAsync(Guid runId);

    Task<IReadOnlyList<EventLog>> GetByRunIdAsync(Guid runId);

    /// <summary>
    /// The run's events of the given <paramref name="types"/> with an Id above
    /// <paramref name="afterId"/> and at most <paramref name="throughId"/>, in Id order.
    /// </summary>
    Task<IReadOnlyList<EventLog>> GetByRunIdAfterAsync(Guid runId, long afterId, long throughId, IReadOnlyCollection<EventType> types);

    /// <summary>The highest Id among the run's events above <paramref name="afterId"/>, or null when there are none.</summary>
    Task<long?> GetLastIdByRunIdAfterAsync(Guid runId, long afterId);

    Task<IReadOnlyList<EventLog>> GetByRunIdLastNAsync(Guid runId, int n);

    /// <summary>Up to <paramref name="limit"/> of the run's events with an Id above <paramref name="cursor"/>, in Id order.</summary>
    Task<IReadOnlyList<EventLog>> GetByRunIdAfterCursorAsync(Guid runId, long cursor, int limit);
}
