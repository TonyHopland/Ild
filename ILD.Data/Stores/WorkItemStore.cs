using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public class WorkItemStore : IWorkItemStore
{
    private readonly AppDbContext _db;

    public WorkItemStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<WorkItem?> GetByIdAsync(Guid id)
        => await _db.WorkItems
            .Include(w => w.LoopTemplateVersion!)
                .ThenInclude(v => v.LoopTemplate)
            .FirstOrDefaultAsync(w => w.Id == id);

    public async Task<IReadOnlyList<WorkItem>> GetByStatusAsync(WorkItemStatus status)
        => await _db.WorkItems
            .Where(w => w.Status == status)
            .Include(w => w.LoopTemplateVersion!)
                .ThenInclude(v => v.LoopTemplate)
            .ToListAsync();

    public async Task<IReadOnlyList<WorkItem>> GetByRepositoryAsync(Guid repositoryId)
        => await _db.WorkItems.Where(w => w.RepositoryId == repositoryId).ToListAsync();

    public async Task<IReadOnlyList<WorkItem>> GetByRepositoryIdsAsync(IReadOnlyList<Guid> repositoryIds)
        => await _db.WorkItems.Where(w => repositoryIds.Contains(w.RepositoryId)).ToListAsync();

    public async Task<IReadOnlyList<WorkItem>> GetByIdsAsync(IReadOnlyList<Guid> ids)
        => ids.Count == 0
            ? Array.Empty<WorkItem>()
            : await _db.WorkItems.Where(w => ids.Contains(w.Id)).ToListAsync();

    public async Task CreateAsync(WorkItem workItem)
    {
        _db.WorkItems.Add(workItem);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(WorkItem workItem)
    {
        _db.WorkItems.Update(workItem);
        await _db.SaveChangesAsync();
    }

    public Task<bool> HasRunningRunAsync(Guid workItemId)
        => _db.LoopRuns.AnyAsync(r => r.WorkItemId == workItemId && r.Status == LoopRunStatus.Running);

    public Task<bool> HasFailedRunAsync(Guid workItemId)
        => _db.LoopRuns.AnyAsync(r => r.WorkItemId == workItemId && r.Status == LoopRunStatus.Failed);

    public async Task<LoopTemplateVersion?> GetLatestTemplateVersionAsync(Guid templateId)
        => await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync();

    public async Task<Repository?> GetRepositoryAsync(Guid id)
        => await _db.Repositories.FindAsync(id).AsTask();

    public async Task<bool> DeleteAsync(Guid id)
    {
        var wi = await _db.WorkItems.FindAsync(id);
        if (wi == null) return false;

        // Get all LoopRuns for this work item so we can clean up their EventLogs
        var loopRuns = await _db.LoopRuns
            .Where(r => r.WorkItemId == id)
            .ToListAsync();
        var loopRunIds = loopRuns.Select(r => r.Id).ToList();

        // Delete EventLog entries for all LoopRuns (no cascade configured)
        if (loopRunIds.Count > 0)
        {
            var eventLogs = await _db.EventLogs
                .Where(e => loopRunIds.Contains(e.LoopRunId ?? Guid.Empty))
                .ToListAsync();
            _db.EventLogs.RemoveRange(eventLogs);
        }

        // Break the circular FK: WorkItem.CurrentLoopRunId -> LoopRun -> WorkItem
        wi.CurrentLoopRunId = null;

        _db.WorkItems.Remove(wi);
        await _db.SaveChangesAsync();
        return true;
    }
}
