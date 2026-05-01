using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class WorkItemManager : IWorkItemManager
{
    private readonly IWorkItemStore _store;
    private readonly IRepositoryManager _repoManager;
    private readonly IEventLogService _eventLog;
    private readonly ILoopRunStore _loopRunStore;
    private readonly IWorkItemNotifier _notifier;

    public WorkItemManager(IWorkItemStore store, IRepositoryManager repoManager, IEventLogService eventLog, ILoopRunStore loopRunStore, IWorkItemNotifier? notifier = null)
    {
        _store = store;
        _repoManager = repoManager;
        _eventLog = eventLog;
        _loopRunStore = loopRunStore;
        _notifier = notifier ?? new NoopWorkItemNotifier();
    }

    public async Task<Guid> CreateWorkItemAsync(string title, string description, Guid? loopTemplateId, Guid? repositoryId)
    {
        if (repositoryId == null)
            throw new InvalidOperationException("RepositoryId is required");

        var repo = await _store.GetRepositoryAsync(repositoryId.Value);
        if (repo == null)
            throw new InvalidOperationException("Repository not found");

        Guid? versionId = null;
        if (loopTemplateId.HasValue)
        {
            var latest = await _store.GetLatestTemplateVersionAsync(loopTemplateId.Value);
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
        await _store.CreateAsync(wi);
        return wi.Id;
    }

    public async Task<WorkItem?> GetWorkItemAsync(Guid workItemId)
        => await _store.GetByIdAsync(workItemId);

    public async Task<bool> UpdateAsync(Guid workItemId, string title, string description)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        wi.Title = title;
        wi.Description = description;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<IEnumerable<WorkItem>> GetWorkItemsByStatusAsync(WorkItemStatus status)
        => await _store.GetByStatusAsync(status);

    public async Task<bool> TransitionToWorkQueueAsync(Guid workItemId)
    {
        var ok = await SetStatusAsync(workItemId, WorkItemStatus.WorkQueue,
            from => from == WorkItemStatus.Backlog || from == WorkItemStatus.HumanFeedback);
        if (!ok) return false;

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
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        var prev = wi.Status;
        wi.Status = WorkItemStatus.HumanFeedback;
        wi.HumanFeedbackReason = reason;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        await _notifier.WorkItemStateChangedAsync(workItemId, prev, WorkItemStatus.HumanFeedback);
        await _notifier.HumanFeedbackRequiredAsync(workItemId, reason);
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

        return await _store.AddDependencyAsync(workItemId, dependsOnWorkItemId);
    }

    public async Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId)
        => await _store.RemoveDependencyAsync(workItemId, dependsOnWorkItemId);

    public async Task<IEnumerable<WorkItem>> GetDependenciesAsync(Guid workItemId)
    {
        var ids = await _store.GetDependencyIdsAsync(workItemId);
        return await _store.GetByIdsAsync(ids.ToList());
    }

    public async Task<IEnumerable<WorkItem>> GetDependentsAsync(Guid workItemId)
    {
        var ids = await _store.GetDependentIdsAsync(workItemId);
        return await _store.GetByIdsAsync(ids.ToList());
    }

    public async Task<bool> IsReadyAsync(Guid workItemId)
    {
        var depIds = await _store.GetDependencyIdsAsync(workItemId);
        var depIdList = depIds.ToList();
        if (depIdList.Count == 0) return true;

        var deps = await _store.GetByIdsAsync(depIdList);
        foreach (var w in deps)
        {
            if (!w.IsPrMerged && w.Status != WorkItemStatus.Done)
                return false;
        }
        return true;
    }

    public async Task<bool> LinkPullRequestAsync(Guid workItemId, string prUrl)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        wi.PrUrl = prUrl;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<bool> ManuallyMarkMergedAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        wi.IsPrMerged = true;
        wi.UpdatedAt = DateTime.UtcNow;

        if (!await _store.HasRunningRunAsync(workItemId))
            wi.Status = WorkItemStatus.Done;

        await _store.UpdateAsync(wi);

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
        var wi = await _store.GetByIdAsync(id);
        if (wi == null) return false;
        if (!isAllowedFrom(wi.Status)) return false;
        var prev = wi.Status;
        wi.Status = next;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        await _notifier.WorkItemStateChangedAsync(id, prev, next);
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
            var nextDeps = await _store.GetDependencyIdsAsync(cur);
            foreach (var n in nextDeps) stack.Push(n);
        }
        return false;
    }

    public async Task<bool> CleanupToDoneAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;

        if (!string.IsNullOrEmpty(wi.WorktreePath))
        {
            await _repoManager.DestroyWorktreeAsync(wi.WorktreePath);
            wi.WorktreePath = null;
        }

        wi.Status = WorkItemStatus.Done;
        wi.HumanFeedbackReason = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<bool> CleanupToBacklogAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;

        if (!string.IsNullOrEmpty(wi.WorktreePath))
        {
            await _repoManager.DestroyWorktreeAsync(wi.WorktreePath);
            wi.WorktreePath = null;
        }

        wi.Status = WorkItemStatus.Backlog;
        wi.HumanFeedbackReason = null;
        wi.CurrentLoopRunId = null;
        wi.BranchName = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<bool> SubmitHumanFeedbackInputAsync(Guid workItemId, string input)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        if (wi.CurrentLoopRunId == null) return false;

        var runId = wi.CurrentLoopRunId.Value;
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run != null)
        {
            var nodes = await _loopRunStore.GetRunNodesAsync(runId);
            var humanRunNode = nodes
                .Where(n => n.LoopNode?.NodeType == NodeType.Human || n.LoopNodeId == run.CurrentNodeId)
                .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
                .FirstOrDefault(n => n.Status == LoopRunNodeStatus.WaitingHuman);
            if (humanRunNode != null)
            {
                humanRunNode.Status = LoopRunNodeStatus.Succeeded;
                humanRunNode.Output = input;
                humanRunNode.CompletedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunNodeAsync(humanRunNode);
            }
        }

        await _eventLog.AppendAsync(runId, "HumanFeedbackReceived", input);

        wi.Status = WorkItemStatus.Running;
        wi.HumanFeedbackReason = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<bool> RejectHumanFeedbackAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        if (wi.CurrentLoopRunId == null) return false;

        var run = await _loopRunStore.GetByIdAsync(wi.CurrentLoopRunId.Value);
        if (run == null) return false;

        var nodes = await _loopRunStore.GetRunNodesAsync(run.Id);
        var currentRunNode = nodes.FirstOrDefault(n => n.LoopNodeId == run.CurrentNodeId);
        if (currentRunNode != null)
        {
            currentRunNode.Status = LoopRunNodeStatus.Failed;
            await _loopRunStore.UpdateRunNodeAsync(currentRunNode);
        }

        await _eventLog.AppendAsync(run.Id, "HumanFeedbackReceived", "rejected by user");

        wi.Status = WorkItemStatus.Running;
        wi.HumanFeedbackReason = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }
}
