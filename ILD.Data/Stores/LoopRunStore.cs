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
    {
        return await _db.LoopRuns
            .Include(r => r.LoopTemplateVersion)
                .ThenInclude(v => v!.LoopTemplate)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

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
        => await _db.LoopRunNodes
            .Where(rn => rn.LoopRunId == runId)
            .OrderBy(rn => rn.CreatedAt)
            .ToListAsync();

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
        // Use Entry(...).State = Modified instead of DbSet.Update to mark
        // only the root entity Modified. DbSet.Update walks the entity
        // graph, attaching reachable navigation entities (e.g.
        // LoopTemplateVersion, RunNodes) as Modified too — and EF's
        // relationship fixup also pulls in any navigation already linked
        // via POCO references from prior scopes (see e.g. LoopRunNode.LoopRun
        // populated by an earlier per-attempt scope), causing those stale
        // entities to be saved back over fresh data (in particular wiping
        // SessionsJson back to its loaded NULL value after the AI node
        // had just persisted a session ID).
        var entry = _db.Entry(run);
        if (entry.State == EntityState.Detached)
            _db.LoopRuns.Attach(run);
        _db.Entry(run).State = EntityState.Modified;
        await _db.SaveChangesAsync();
    }

    public async Task CreateRunNodeAsync(LoopRunNode runNode)
    {
        _db.LoopRunNodes.Add(runNode);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateRunNodeAsync(LoopRunNode runNode)
    {
        // Same rationale as UpdateRunAsync: avoid DbSet.Update graph
        // traversal which would drag a navigation-linked LoopRun (with
        // stale SessionsJson) into the new context as Modified and clobber
        // it on save.
        var entry = _db.Entry(runNode);
        if (entry.State == EntityState.Detached)
            _db.LoopRunNodes.Attach(runNode);
        _db.Entry(runNode).State = EntityState.Modified;
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

    public async Task<int> AllocateNextEventSequenceAsync(Guid runId)
    {
        // Per-run sequence allocator. Callers are expected to serialize calls
        // for the same runId via an in-memory lock (see EventLogService).
        // Cross-run calls remain concurrent.
        var run = await _db.LoopRuns.FirstOrDefaultAsync(r => r.Id == runId)
            ?? throw new InvalidOperationException($"Run {runId} not found while allocating event sequence");
        run.NextEventSeq += 1;
        await _db.SaveChangesAsync();
        return run.NextEventSeq;
    }
}
