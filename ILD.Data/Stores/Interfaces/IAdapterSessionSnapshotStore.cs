using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

public interface IAdapterSessionSnapshotStore
{
    Task<AdapterSessionSnapshot?> GetAsync(Guid loopRunId, string adapterName, string sessionId, CancellationToken cancellationToken = default);
    Task UpsertAsync(Guid loopRunId, string adapterName, string sessionId, string sessionJson, CancellationToken cancellationToken = default);

    /// <summary>Fetch a snapshot owned by a <see cref="ChatSession"/> instead of a LoopRun.</summary>
    Task<AdapterSessionSnapshot?> GetForChatAsync(Guid chatSessionId, string adapterName, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Upsert a snapshot owned by a <see cref="ChatSession"/> instead of a LoopRun.</summary>
    Task UpsertForChatAsync(Guid chatSessionId, string adapterName, string sessionId, string sessionJson, CancellationToken cancellationToken = default);
}
