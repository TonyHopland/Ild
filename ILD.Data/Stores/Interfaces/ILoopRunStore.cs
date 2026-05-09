using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface ILoopRunStore
{
    Task<LoopRun?> GetByIdAsync(Guid id);
    Task<LoopRun?> GetByPrUrlAsync(string prUrl);
    Task<LoopRun?> GetByWorkItemAsync(Guid workItemId);
    Task<IReadOnlyList<LoopRun>> GetAllByWorkItemAsync(Guid workItemId);
    Task<LoopRun?> GetCurrentByWorkItemAsync(Guid workItemId);
    Task<IReadOnlyList<LoopRun>> GetAllAsync(int skip = 0, int take = 100);
    Task<IReadOnlyList<LoopRun>> GetRunningRunsAsync();
    Task<IReadOnlyList<LoopRunNode>> GetRunNodesAsync(Guid runId);
    Task<LoopRunNode?> GetRunNodeAsync(Guid runId, Guid nodeId);
    Task<LoopRunNode?> GetRunNodeByIdAsync(Guid runNodeId);
    Task CreateRunAsync(LoopRun run);
    Task UpdateRunAsync(LoopRun run);
    Task CreateRunNodeAsync(LoopRunNode runNode);
    Task UpdateRunNodeAsync(LoopRunNode runNode);
    Task PersistEdgeTraversalAsync(Guid runId, Guid edgeId, int count);
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
