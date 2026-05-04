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

    public async Task<bool> AddDependencyAsync(Guid workItemId, Guid dependencyWorkItemId)
    {
        var exists = await _db.WorkItemDependencies
            .AnyAsync(d => d.WorkItemId == workItemId && d.DependencyWorkItemId == dependencyWorkItemId);
        if (exists) return false;

        _db.WorkItemDependencies.Add(new WorkItemDependency
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            DependencyWorkItemId = dependencyWorkItemId,
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependencyWorkItemId)
    {
        var dep = await _db.WorkItemDependencies
            .FirstOrDefaultAsync(d => d.WorkItemId == workItemId && d.DependencyWorkItemId == dependencyWorkItemId);
        if (dep == null) return false;
        _db.WorkItemDependencies.Remove(dep);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<Guid>> GetDependencyIdsAsync(Guid workItemId)
        => await _db.WorkItemDependencies
            .Where(d => d.WorkItemId == workItemId)
            .Select(d => d.DependencyWorkItemId)
            .ToListAsync();

    public async Task<IReadOnlyList<Guid>> GetDependentIdsAsync(Guid workItemId)
        => await _db.WorkItemDependencies
            .Where(d => d.DependencyWorkItemId == workItemId)
            .Select(d => d.WorkItemId)
            .ToListAsync();

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

        // Remove dependency records that reference this work item (both directions)
        var deps = await _db.WorkItemDependencies
            .Where(d => d.WorkItemId == id || d.DependencyWorkItemId == id)
            .ToListAsync();
        _db.WorkItemDependencies.RemoveRange(deps);

        _db.WorkItems.Remove(wi);
        await _db.SaveChangesAsync();
        return true;
    }
}
