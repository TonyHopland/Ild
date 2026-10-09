using ILD.Data.Entities;

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

    /// <summary>Whether the run's run-ending event (completed, failed, cancelled) has been written.</summary>
    Task<bool> HasEndedAsync(Guid runId);

    Task<IReadOnlyList<EventLog>> GetByRunIdAsync(Guid runId);
    Task<IReadOnlyList<EventLog>> GetByRunIdLastNAsync(Guid runId, int n);

    /// <summary>Up to <paramref name="limit"/> of the run's events with an Id above <paramref name="cursor"/>, in Id order.</summary>
    Task<IReadOnlyList<EventLog>> GetByRunIdAfterCursorAsync(Guid runId, long cursor, int limit);
}
