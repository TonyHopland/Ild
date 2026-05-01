using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface ILoopRunStore
{
    Task<LoopRun?> GetByIdAsync(Guid id);
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
}
