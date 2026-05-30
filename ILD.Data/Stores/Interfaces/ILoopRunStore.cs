using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface ILoopRunStore
{
    Task<LoopRun?> GetByIdAsync(Guid id);
    Task<LoopRun?> GetByPrUrlAsync(string prUrl);
    Task<LoopRun?> GetByWorkItemAsync(string workItemId);
    Task<IReadOnlyList<LoopRun>> GetAllByWorkItemAsync(string workItemId);
    Task<IReadOnlyList<LoopRun>> GetByWorkItemPagedAsync(string workItemId, int skip, int take);
    Task<LoopRun?> GetCurrentByWorkItemAsync(string workItemId);
    Task<IReadOnlyList<LoopRun>> GetAllAsync(int skip = 0, int take = 100);
    Task<IReadOnlyList<LoopRun>> GetRunningRunsAsync();
    Task<IReadOnlyList<LoopRunNode>> GetRunNodesAsync(Guid runId);
    /// <summary>Run nodes for a run with their <c>LoopNode</c> eager-loaded (left join — may be null if the template node was since removed).</summary>
    Task<IReadOnlyList<LoopRunNode>> GetRunNodesWithNodeAsync(Guid runId);
    Task<IReadOnlyList<AdapterSessionSnapshot>> GetSessionSnapshotsAsync(Guid runId);
    Task<IReadOnlyList<LoopRunSessionBinding>> GetSessionBindingsAsync(Guid runId);
    Task<LoopRunSessionBinding?> GetSessionBindingAsync(Guid runId, string adapterName, string placeholderId);
    Task UpsertSessionBindingAsync(Guid runId, string adapterName, string placeholderId, string sessionId);
    Task<LoopRunNode?> GetRunNodeAsync(Guid runId, Guid nodeId);
    Task<LoopRunNode?> GetRunNodeByIdAsync(Guid runNodeId);
    Task CreateRunAsync(LoopRun run);
    Task UpdateRunAsync(LoopRun run);
    Task CreateRunNodeAsync(LoopRunNode runNode);
    Task UpdateRunNodeAsync(LoopRunNode runNode);
    Task DeleteRunNodeAsync(Guid runNodeId);
    Task<LoopNode?> GetStartNodeAsync(Guid versionId);
    Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId);
    Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForNodeIdsAsync(IReadOnlyList<Guid> nodeIds);
    Task<bool> HasFailureEdgeAsync(Guid nodeId);
    Task<LoopNodeEdge?> GetEdgeAsync(Guid edgeId);
    Task<IReadOnlyList<Guid>> GetFailedRunIdsAsync();

    /// <summary>
    /// Atomically increments and returns the next per-run event log sequence
    /// number. Replaces the previous global lock + MAX(Sequence) scan.
    /// </summary>
    Task<int> AllocateNextEventSequenceAsync(Guid runId);

    /// <summary>
    /// Hard-deletes a loop run and all of its dependent rows (run nodes,
    /// edge traversals, event log entries). Returns false if the run does
    /// not exist or is still Running.
    /// </summary>
    Task<bool> DeleteAsync(Guid runId);
}
