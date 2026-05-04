using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public class LoopRunStore : ILoopRunStore
{
    private readonly AppDbContext _db;

    public LoopRunStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<LoopRun?> GetByIdAsync(Guid id)
        => await _db.LoopRuns.FindAsync(id).AsTask();

    public async Task<LoopRun?> GetByWorkItemAsync(Guid workItemId)
        => await _db.LoopRuns.FirstOrDefaultAsync(r => r.WorkItemId == workItemId);

    public async Task<IReadOnlyList<LoopRun>> GetAllByWorkItemAsync(Guid workItemId)
        => await _db.LoopRuns
            .Where(r => r.WorkItemId == workItemId)
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .ToListAsync();

    public async Task<LoopRun?> GetCurrentByWorkItemAsync(Guid workItemId)
        => await _db.LoopRuns
            .Where(r => r.WorkItemId == workItemId && r.Status == LoopRunStatus.Running)
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<LoopRun>> GetAllAsync(int skip = 0, int take = 100)
        => await _db.LoopRuns
            .Include(r => r.RunNodes).ThenInclude(rn => rn.LoopNode)
            .Include(r => r.LoopTemplateVersion)
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .Skip(skip).Take(take)
            .ToListAsync();

    public async Task<IReadOnlyList<LoopRun>> GetRunningRunsAsync()
        => await _db.LoopRuns.Where(r => r.Status == LoopRunStatus.Running).ToListAsync();

    public async Task<IReadOnlyList<LoopRunNode>> GetRunNodesAsync(Guid runId)
        => await _db.LoopRunNodes.Where(rn => rn.LoopRunId == runId).ToListAsync();

    public async Task<LoopRunNode?> GetRunNodeAsync(Guid runId, Guid nodeId)
        => await _db.LoopRunNodes
            .Where(rn => rn.LoopRunId == runId && rn.LoopNodeId == nodeId)
            .OrderByDescending(rn => rn.StartedAt ?? rn.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<LoopRunNode?> GetRunNodeByIdAsync(Guid runNodeId)
        => await _db.LoopRunNodes.FindAsync(runNodeId).AsTask();

    public async Task CreateRunAsync(LoopRun run)
    {
        _db.LoopRuns.Add(run);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateRunAsync(LoopRun run)
    {
        _db.LoopRuns.Update(run);
        await _db.SaveChangesAsync();
    }

    public async Task CreateRunNodeAsync(LoopRunNode runNode)
    {
        _db.LoopRunNodes.Add(runNode);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateRunNodeAsync(LoopRunNode runNode)
    {
        _db.LoopRunNodes.Update(runNode);
        await _db.SaveChangesAsync();
    }

    public async Task PersistEdgeTraversalAsync(Guid runId, Guid edgeId, int count)
    {
        var existing = await _db.LoopRunEdgeTraversals
            .FirstOrDefaultAsync(t => t.LoopRunId == runId && t.EdgeId == edgeId);
        if (existing == null)
        {
            _db.LoopRunEdgeTraversals.Add(new LoopRunEdgeTraversal
            {
                Id = Guid.NewGuid(),
                LoopRunId = runId,
                EdgeId = edgeId,
                TraversalCount = count,
            });
        }
        else
        {
            existing.TraversalCount = count;
        }
        await _db.SaveChangesAsync();
    }

    public async Task<LoopNode?> GetStartNodeAsync(Guid versionId)
        => await _db.LoopNodes.FirstOrDefaultAsync(n => n.LoopTemplateVersionId == versionId && n.NodeType == NodeType.Start);

    public async Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId)
        => await _db.LoopNodes.Where(n => n.LoopTemplateVersionId == versionId).ToListAsync();

    public async Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForNodeIdsAsync(IReadOnlyList<Guid> nodeIds)
        => await _db.LoopNodeEdges.Where(e => nodeIds.Contains(e.SourceNodeId)).ToListAsync();

    public Task<bool> HasFailureEdgeAsync(Guid nodeId)
        => _db.LoopNodeEdges.AnyAsync(e => e.SourceNodeId == nodeId && e.EdgeType == EdgeType.OnFailure);

    public async Task<LoopNodeEdge?> GetEdgeAsync(Guid edgeId)
        => await _db.LoopNodeEdges.FindAsync(edgeId).AsTask();

    public async Task<IReadOnlyList<Guid>> GetFailedRunIdsAsync()
        => await _db.LoopRuns
            .Where(r => r.Status == LoopRunStatus.Failed)
            .Select(r => r.Id)
            .ToListAsync();
}
