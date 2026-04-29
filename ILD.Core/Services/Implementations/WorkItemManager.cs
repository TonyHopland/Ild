using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public class WorkItemManager : IWorkItemManager
{
    private readonly AppDbContext _db;

    public WorkItemManager(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> CreateWorkItemAsync(string title, string description, Guid? loopTemplateId, Guid? repositoryId)
    {
        if (repositoryId == null)
            throw new InvalidOperationException("RepositoryId is required");

        var repo = await _db.Repositories.FindAsync(repositoryId.Value);
        if (repo == null)
            throw new InvalidOperationException("Repository not found");

        Guid? versionId = null;
        if (loopTemplateId.HasValue)
        {
            var latest = await _db.LoopTemplateVersions
                .Where(v => v.LoopTemplateId == loopTemplateId.Value)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync();
            versionId = latest?.Id;
        }

        var wi = new WorkItem
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Priority = WorkItemPriority.Medium,
            Status = repo.DefaultIntakeStatus,
            RepositoryId = repositoryId.Value,
            LoopTemplateVersionId = versionId,
        };
        _db.WorkItems.Add(wi);
        await _db.SaveChangesAsync();
        return wi.Id;
    }

    public async Task<WorkItem?> GetWorkItemAsync(Guid workItemId)
        => await _db.WorkItems.FindAsync(workItemId);

    public async Task<IEnumerable<WorkItem>> GetWorkItemsByStatusAsync(WorkItemStatus status)
        => await _db.WorkItems.Where(w => w.Status == status).ToListAsync();

    public async Task<bool> TransitionToWorkQueueAsync(Guid workItemId)
    {
        var ok = await SetStatusAsync(workItemId, WorkItemStatus.WorkQueue,
            from => from == WorkItemStatus.Backlog || from == WorkItemStatus.HumanFeedback);
        if (!ok) return false;

        // Auto-promote to Ready if all dependencies satisfied
        if (await IsReadyAsync(workItemId))
            await TransitionToReadyAsync(workItemId);
        return true;
    }

    public async Task<bool> TransitionToReadyAsync(Guid workItemId)
    {
        if (!await IsReadyAsync(workItemId)) return false;
        return await SetStatusAsync(workItemId, WorkItemStatus.Ready,
            from => from == WorkItemStatus.WorkQueue || from == WorkItemStatus.Backlog);
    }

    public async Task<bool> TransitionToRunningAsync(Guid workItemId)
        => await SetStatusAsync(workItemId, WorkItemStatus.Running,
            from => from == WorkItemStatus.Ready || from == WorkItemStatus.HumanFeedback);

    public async Task<bool> TransitionToHumanFeedbackAsync(Guid workItemId, string reason)
    {
        var wi = await _db.WorkItems.FindAsync(workItemId);
        if (wi == null) return false;
        wi.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = reason;
        wi.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> TransitionToDoneAsync(Guid workItemId)
        => await SetStatusAsync(workItemId, WorkItemStatus.Done, _ => true);

    public async Task<bool> AddDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId)
    {
        if (workItemId == dependsOnWorkItemId)
            throw new InvalidOperationException("A work item cannot depend on itself.");

        if (await WouldCreateCycle(workItemId, dependsOnWorkItemId))
            throw new InvalidOperationException("Adding this dependency would create a cycle.");

        var exists = await _db.WorkItemDependencies
            .AnyAsync(d => d.WorkItemId == workItemId && d.DependencyWorkItemId == dependsOnWorkItemId);
        if (exists) return false;

        _db.WorkItemDependencies.Add(new WorkItemDependency
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            DependencyWorkItemId = dependsOnWorkItemId,
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId)
    {
        var dep = await _db.WorkItemDependencies
            .FirstOrDefaultAsync(d => d.WorkItemId == workItemId && d.DependencyWorkItemId == dependsOnWorkItemId);
        if (dep == null) return false;
        _db.WorkItemDependencies.Remove(dep);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<IEnumerable<WorkItem>> GetDependenciesAsync(Guid workItemId)
    {
        var ids = await _db.WorkItemDependencies
            .Where(d => d.WorkItemId == workItemId)
            .Select(d => d.DependencyWorkItemId)
            .ToListAsync();
        return await _db.WorkItems.Where(w => ids.Contains(w.Id)).ToListAsync();
    }

    public async Task<IEnumerable<WorkItem>> GetDependentsAsync(Guid workItemId)
    {
        var ids = await _db.WorkItemDependencies
            .Where(d => d.DependencyWorkItemId == workItemId)
            .Select(d => d.WorkItemId)
            .ToListAsync();
        return await _db.WorkItems.Where(w => ids.Contains(w.Id)).ToListAsync();
    }

    public async Task<bool> IsReadyAsync(Guid workItemId)
    {
        var depIds = await _db.WorkItemDependencies
            .Where(d => d.WorkItemId == workItemId)
            .Select(d => d.DependencyWorkItemId)
            .ToListAsync();
        if (depIds.Count == 0) return true;

        var unmergedExists = await _db.WorkItems
            .Where(w => depIds.Contains(w.Id))
            .AnyAsync(w => !w.IsPrMerged && w.Status != WorkItemStatus.Done);
        return !unmergedExists;
    }

    public async Task<bool> LinkPullRequestAsync(Guid workItemId, string prUrl)
    {
        var wi = await _db.WorkItems.FindAsync(workItemId);
        if (wi == null) return false;
        wi.PrUrl = prUrl;
        wi.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ManuallyMarkMergedAsync(Guid workItemId)
    {
        var wi = await _db.WorkItems.FindAsync(workItemId);
        if (wi == null) return false;
        wi.IsPrMerged = true;
        wi.UpdatedAt = DateTime.UtcNow;

        // Only set Done directly if there's no active LoopRun.
        // When a run is active, the engine handles the transition via Cleanup node.
        var activeRun = await _db.LoopRuns
            .FirstOrDefaultAsync(r => r.WorkItemId == workItemId && r.Status == LoopRunStatus.Running);

        if (activeRun == null)
            wi.Status = WorkItemStatus.Done;

        await _db.SaveChangesAsync();

        var dependents = await GetDependentsAsync(workItemId);
        foreach (var d in dependents)
        {
            if (d.Status == WorkItemStatus.WorkQueue && await IsReadyAsync(d.Id))
                await TransitionToReadyAsync(d.Id);
        }
        return true;
    }

    private async Task<bool> SetStatusAsync(Guid id, WorkItemStatus next, Func<WorkItemStatus, bool> isAllowedFrom)
    {
        var wi = await _db.WorkItems.FindAsync(id);
        if (wi == null) return false;
        if (!isAllowedFrom(wi.Status)) return false;
        wi.Status = next;
        wi.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task<bool> WouldCreateCycle(Guid workItemId, Guid newDepId)
    {
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(newDepId);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!visited.Add(cur)) continue;
            if (cur == workItemId) return true;
            var nextDeps = await _db.WorkItemDependencies
                .Where(d => d.WorkItemId == cur)
                .Select(d => d.DependencyWorkItemId)
                .ToListAsync();
            foreach (var n in nextDeps) stack.Push(n);
        }
        return false;
    }
}
