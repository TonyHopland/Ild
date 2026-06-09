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

    public async Task<LoopRun?> GetByPrUrlAsync(string prUrl)
        => await _db.LoopRuns.FirstOrDefaultAsync(r => r.PrUrl == prUrl);

    public async Task<LoopRun?> GetByWorkItemAsync(string workItemId)
        => await _db.LoopRuns.FirstOrDefaultAsync(r => r.WorkItemId == workItemId);

    public async Task<IReadOnlyList<LoopRun>> GetAllByWorkItemAsync(string workItemId)
        => await _db.LoopRuns
            .Where(r => r.WorkItemId == workItemId)
            .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<LoopRun>> GetByWorkItemPagedAsync(string workItemId, int skip, int take)
        => await _db.LoopRuns.AsNoTracking()
            .Where(r => r.WorkItemId == workItemId)
            .OrderByDescending(r => r.StartedAt)
            .Skip(skip).Take(take)
            .ToListAsync();

    public async Task<LoopRun?> GetCurrentByWorkItemAsync(string workItemId)
        => await _db.LoopRuns
            .Where(r => r.WorkItemId == workItemId && (r.Status == LoopRunStatus.Running
                || r.Status == LoopRunStatus.Failed
                || r.Status == LoopRunStatus.Cancelled
                || r.Status == LoopRunStatus.WaitingHuman))
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

    public async Task<IReadOnlyList<LoopRun>> GetActiveRunsAsync()
        => await _db.LoopRuns
            .Where(r => r.Status == LoopRunStatus.Running || r.Status == LoopRunStatus.WaitingHuman)
            .ToListAsync();

    public async Task<IReadOnlyList<LoopRun>> GetReclaimableRunsAsync(DateTime cutoff, int take = 200)
        => await _db.LoopRuns.AsNoTracking()
            .Where(r => !r.Retain
                && (r.Status == LoopRunStatus.Completed
                    || r.Status == LoopRunStatus.Failed
                    || r.Status == LoopRunStatus.Cancelled)
                && r.CompletedAt != null
                && r.CompletedAt < cutoff)
            .OrderBy(r => r.CompletedAt)
            .Take(take)
            .ToListAsync();

    public async Task<IReadOnlyList<LoopRunNode>> GetRunNodesAsync(Guid runId)
        => await _db.LoopRunNodes
            .Where(rn => rn.LoopRunId == runId)
            .OrderBy(rn => rn.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<LoopRunNode>> GetRunNodesWithNodeAsync(Guid runId)
        => await _db.LoopRunNodes
            .Include(rn => rn.LoopNode)
            .Where(rn => rn.LoopRunId == runId)
            .OrderBy(rn => rn.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<AdapterSessionSnapshot>> GetSessionSnapshotsAsync(Guid runId)
        => await _db.AdapterSessionSnapshots
            .Where(s => s.LoopRunId == runId)
            .OrderByDescending(s => s.UpdatedAt ?? s.CreatedAt)
            .ThenBy(s => s.AdapterName)
            .ThenBy(s => s.SessionId)
            .ToListAsync();

    public async Task<IReadOnlyList<LoopRunSessionBinding>> GetSessionBindingsAsync(Guid runId)
        => await _db.LoopRunSessionBindings
            .Where(s => s.LoopRunId == runId)
            .OrderBy(s => s.AdapterName)
            .ThenBy(s => s.PlaceholderId)
            .ToListAsync();

    public async Task<LoopRunSessionBinding?> GetSessionBindingAsync(Guid runId, string adapterName, string placeholderId)
        => await _db.LoopRunSessionBindings.FirstOrDefaultAsync(s =>
            s.LoopRunId == runId
            && s.AdapterName == adapterName
            && s.PlaceholderId == placeholderId);

    public async Task UpsertSessionBindingAsync(Guid runId, string adapterName, string placeholderId, string sessionId)
    {
        var existing = await GetSessionBindingAsync(runId, adapterName, placeholderId);
        if (existing is null)
        {
            _db.LoopRunSessionBindings.Add(new LoopRunSessionBinding
            {
                LoopRunId = runId,
                AdapterName = adapterName,
                PlaceholderId = placeholderId,
                SessionId = sessionId,
            });
        }
        else
        {
            existing.SessionId = sessionId;
        }

        await _db.SaveChangesAsync();
    }

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

    public async Task ReloadAsync(LoopRun run)
    {
        // Refresh a (possibly long-held) tracked instance with the row's
        // current column values. A plain re-query is not enough: EF identity
        // resolution returns the already-tracked instance with its in-memory
        // values, so control-plane fields written by other scopes (IsPaused,
        // Retain, Status=Cancelled) stay invisible without an explicit reload.
        var entry = _db.Entry(run);
        if (entry.State == EntityState.Detached)
        {
            _db.LoopRuns.Attach(run);
            entry = _db.Entry(run);
        }
        await entry.ReloadAsync();
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
        // entities to be saved back over fresh data.
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
        // traversal which would drag a navigation-linked LoopRun into the
        // new context as Modified and clobber it on save.
        var entry = _db.Entry(runNode);
        if (entry.State == EntityState.Detached)
            _db.LoopRunNodes.Attach(runNode);
        _db.Entry(runNode).State = EntityState.Modified;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteRunNodeAsync(Guid runNodeId)
    {
        var existing = await _db.LoopRunNodes.FindAsync(runNodeId);
        if (existing == null) return;
        _db.LoopRunNodes.Remove(existing);
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

    public async Task<bool> DeleteAsync(Guid runId)
    {
        var run = await _db.LoopRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null) return false;
        if (run.Status == LoopRunStatus.Running) return false;

        var eventLogs = await _db.EventLogs
            .Where(e => e.LoopRunId == runId)
            .ToListAsync();

        // Large event payloads spill to files outside the DB (EventLogService);
        // deleting only the rows would orphan them on disk forever. Best-effort.
        string? payloadDir = null;
        foreach (var entry in eventLogs)
        {
            if (string.IsNullOrEmpty(entry.PayloadPath)) continue;
            try
            {
                if (File.Exists(entry.PayloadPath)) File.Delete(entry.PayloadPath);
                payloadDir ??= Path.GetDirectoryName(entry.PayloadPath);
            }
            catch { /* best effort */ }
        }
        if (payloadDir is not null)
        {
            try
            {
                if (Directory.Exists(payloadDir) && !Directory.EnumerateFileSystemEntries(payloadDir).Any())
                    Directory.Delete(payloadDir);
            }
            catch { /* best effort */ }
        }

        if (eventLogs.Count > 0)
            _db.EventLogs.RemoveRange(eventLogs);

        _db.LoopRuns.Remove(run);
        await _db.SaveChangesAsync();
        return true;
    }
}
