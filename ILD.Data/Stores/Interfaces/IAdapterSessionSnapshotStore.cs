using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

public interface IAdapterSessionSnapshotStore
{
    Task<AdapterSessionSnapshot?> GetAsync(Guid loopRunId, string adapterName, string sessionId, CancellationToken cancellationToken = default);
    Task UpsertAsync(Guid loopRunId, string adapterName, string sessionId, string sessionJson, CancellationToken cancellationToken = default);
}