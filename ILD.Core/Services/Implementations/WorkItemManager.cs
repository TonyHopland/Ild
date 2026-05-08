using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using System.Text.Json;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Remote-backed implementation of <see cref="IWorkItemManager"/>.
/// The standalone WorkItemServer is authoritative for the work-item domain
/// (Title, Description, Status, Priority, Tags, Dependencies, Conversation,
/// CreatedBy). ILD keeps a local sidecar row that mirrors the server view
/// and additionally stores engine-only fields (worktree, branch, PR, current
/// loop run) that the server has no knowledge of.
///
/// Every write call hits the server first and then mirrors the result down
/// to the local row so subsequent reads (engine, controllers, UI) stay
/// consistent without round-tripping the server on every render.
/// </summary>
public class WorkItemManager : IWorkItemManager
{
    private readonly IWorkItemStore _store;
    private readonly IRepositoryManager _repoManager;
    private readonly IEventLogService _eventLog;
    private readonly ILoopRunStore _loopRunStore;
    private readonly IWorkItemNotifier _notifier;
    private readonly IWorkItemServerClient _server;
    private readonly IWorkItemServerOptionsResolver _options;

    public WorkItemManager(
        IWorkItemStore store,
        IRepositoryManager repoManager,
        IEventLogService eventLog,
        ILoopRunStore loopRunStore,
        IWorkItemServerClient server,
        IWorkItemServerOptionsResolver options,
        IWorkItemNotifier? notifier = null)
    {
        _store = store;
        _repoManager = repoManager;
        _eventLog = eventLog;
        _loopRunStore = loopRunStore;
        _server = server;
        _options = options;
        _notifier = notifier ?? new NoopWorkItemNotifier();
    }

    // ──────────────────────────────────────────────────────────────────
    // Create / Read / Update
    // ──────────────────────────────────────────────────────────────────

    public Task<Guid> CreateWorkItemAsync(string title, string description, Guid? loopTemplateId, Guid? repositoryId)
        => CreateWorkItemAsync(title, description, loopTemplateId, repositoryId, null, false);

    public async Task<Guid> CreateWorkItemAsync(
        string title,
        string description,
        Guid? loopTemplateId,
        Guid? repositoryId,
        Guid? createdByLoopRunId,
        bool forceBacklog,
        IEnumerable<string>? tags = null)
    {
        var opts = await _options.ResolveForRepositoryAsync(repositoryId);

        RemoteWorkItemStatus? forceStatus = forceBacklog ? RemoteWorkItemStatus.Backlog : null;
        if (!forceBacklog && repositoryId.HasValue)
        {
            var repo = await _store.GetRepositoryAsync(repositoryId.Value)
                ?? throw new InvalidOperationException("Repository not found");
            forceStatus = MapToRemote(repo.DefaultIntakeStatus);
        }

        var serverWi = await _server.CreateAsync(opts, new RemoteCreateWorkItemRequest
        {
            Title = title,
            Description = description,
            CreatedBy = createdByLoopRunId.HasValue ? $"Agent-{createdByLoopRunId.Value}" : null,
            ForceStatus = forceStatus,
            Tags = tags?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
        });

        Guid? versionId = null;
        if (loopTemplateId.HasValue)
        {
            var latest = await _store.GetLatestTemplateVersionAsync(loopTemplateId.Value);
            versionId = latest?.Id;
        }

        var local = new WorkItem
        {
            Id = serverWi.Id,
            Title = serverWi.Title,
            Description = serverWi.Description,
            Priority = MapFromRemote(serverWi.Priority),
            Status = MapFromRemote(serverWi.Status),
            RepositoryId = repositoryId ?? Guid.Empty,
            LoopTemplateVersionId = versionId,
            CreatedByLoopRunId = createdByLoopRunId,
            CreatedBy = serverWi.CreatedBy,
            CreatedAt = serverWi.CreatedAt,
            UpdatedAt = serverWi.UpdatedAt,
        };
        WriteCachedTags(local, serverWi.Tags);
        WriteCachedConversation(local, serverWi.Conversation);

        await _store.CreateAsync(local);
        return local.Id;
    }

    public async Task<WorkItem?> GetWorkItemAsync(Guid workItemId)
    {
        // Server-authoritative read. Local row is a sidecar for engine-only
        // fields. Title/Status/Priority/Tags/Conversation always come from
        // the server so the UI cannot show stale data when the server is
        // unreachable — the HTTP exception surfaces to the caller.
        var local = await _store.GetByIdAsync(workItemId);
        if (local == null) return null;

        var opts = await _options.ResolveForRepositoryAsync(local.RepositoryId);
        var remote = await _server.GetAsync(opts, workItemId);
        if (remote == null) return null;

        MergeRemoteOntoLocal(local, remote);
        return local;
    }

    public async Task<IReadOnlyList<WorkItem>> ListAsync(
        WorkItemStatus? status,
        Guid? createdByLoopRunId,
        Guid? repositoryId,
        int skip,
        int take)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;

        var opts = await _options.ResolveForRepositoryAsync(repositoryId);
        var remoteStatus = status.HasValue ? MapToRemote(status.Value) : (RemoteWorkItemStatus?)null;
        var remoteList = await _server.ListAsync(opts, remoteStatus, tags: null);
        if (remoteList.Count == 0) return Array.Empty<WorkItem>();

        var ids = remoteList.Select(r => r.Id).ToList();
        var localById = (await _store.GetByIdsAsync(ids)).ToDictionary(w => w.Id);

        var merged = new List<WorkItem>(remoteList.Count);
        foreach (var r in remoteList)
        {
            // Items the server knows about but this ILD instance has no
            // sidecar for belong to another ILD — skip them.
            if (!localById.TryGetValue(r.Id, out var local)) continue;
            if (createdByLoopRunId.HasValue && local.CreatedByLoopRunId != createdByLoopRunId) continue;
            if (repositoryId.HasValue && local.RepositoryId != repositoryId) continue;
            MergeRemoteOntoLocal(local, r);
            merged.Add(local);
        }

        return merged
            .OrderByDescending(w => w.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToList();
    }

    private static void MergeRemoteOntoLocal(WorkItem local, RemoteWorkItem remote)
    {
        local.Title = remote.Title;
        local.Description = remote.Description ?? string.Empty;
        local.Status = MapFromRemote(remote.Status);
        local.Priority = MapFromRemote(remote.Priority);
        local.CreatedBy = remote.CreatedBy;
        local.HumanFeedbackActions = remote.HumanFeedbackActions;
        WriteCachedTags(local, remote.Tags);
        WriteCachedConversation(local, remote.Conversation);
    }

    public async Task<bool> UpdateAsync(Guid workItemId, string title, string description, Guid? loopTemplateId = null)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;

        var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
        var updated = await _server.UpdateAsync(opts, workItemId, new RemoteUpdateWorkItemRequest
        {
            Title = title,
            Description = description,
        });
        if (updated == null) return false;

        wi.Title = updated.Title;
        wi.Description = updated.Description;
        if (loopTemplateId.HasValue)
        {
            var latest = await _store.GetLatestTemplateVersionAsync(loopTemplateId.Value);
            wi.LoopTemplateVersionId = latest?.Id;
        }
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<IEnumerable<WorkItem>> GetWorkItemsByStatusAsync(WorkItemStatus status)
        => await _store.GetByStatusAsync(status);

    // ──────────────────────────────────────────────────────────────────
    // Transitions
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> TransitionToWorkQueueAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        if (wi.Status != WorkItemStatus.Backlog && wi.Status != WorkItemStatus.HumanFeedback)
            return false;

        await TransitionAsync(workItemId, WorkItemStatus.WorkQueue);

        if (await IsReadyAsync(workItemId))
            await TransitionToReadyAsync(workItemId);
        return true;
    }

    public async Task<bool> TransitionToReadyAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        if (!await IsReadyAsync(workItemId)) return false;
        if (wi.Status != WorkItemStatus.WorkQueue && wi.Status != WorkItemStatus.Backlog)
            return false;
        return await TransitionAsync(workItemId, WorkItemStatus.Ready);
    }

    public async Task<bool> TransitionToRunningAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        if (wi.Status != WorkItemStatus.Ready && wi.Status != WorkItemStatus.HumanFeedback)
            return false;
        return await TransitionAsync(workItemId, WorkItemStatus.Running);
    }

    public Task<bool> TransitionToHumanFeedbackAsync(Guid workItemId, string reason)
        => TransitionAsync(workItemId, WorkItemStatus.HumanFeedback, reason);

    public Task<bool> TransitionToDoneAsync(Guid workItemId)
        => TransitionAsync(workItemId, WorkItemStatus.Done);

    public async Task<bool> TransitionAsync(
        Guid workItemId,
        WorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;

        var prev = wi.Status;
        var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
        var resp = await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
        {
            TargetStatus = MapToRemote(targetStatus),
            Reason = reason,
            Actions = actions,
        });

        if (!resp.Success)
            return false;

        var actual = MapFromRemote(resp.ActualStatus);
        wi.Status = actual;

        if (actual == WorkItemStatus.HumanFeedback)
        {
            if (reason != null) wi.HumanFeedbackReason = reason;
            if (actions != null) wi.HumanFeedbackActions = actions;
        }
        else
        {
            wi.HumanFeedbackReason = null;
            wi.HumanFeedbackActions = null;
        }

        if (currentLoopRunId.HasValue)
        {
            wi.CurrentLoopRunId = currentLoopRunId.Value == Guid.Empty
                ? null
                : currentLoopRunId.Value;
        }

        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);

        if (prev != actual)
            await _notifier.WorkItemStateChangedAsync(workItemId, prev, actual);

        if (actual == WorkItemStatus.HumanFeedback && wi.HumanFeedbackReason != null)
            await _notifier.HumanFeedbackRequiredAsync(workItemId, wi.HumanFeedbackReason);

        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Dependencies (server-only)
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> AddDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId)
    {
        if (workItemId == dependsOnWorkItemId)
            throw new InvalidOperationException("A work item cannot depend on itself.");

        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;

        if (await WouldCreateCycle(workItemId, dependsOnWorkItemId))
            throw new InvalidOperationException("Adding this dependency would create a cycle.");

        var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
        return await _server.AddDependencyAsync(opts, workItemId, dependsOnWorkItemId);
    }

    public async Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;

        var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
        return await _server.RemoveDependencyAsync(opts, workItemId, dependsOnWorkItemId);
    }

    public async Task<IEnumerable<WorkItem>> GetDependenciesAsync(Guid workItemId)
    {
        var ids = await GetServerDependencyIdsAsync(workItemId);
        if (ids.Count == 0) return Array.Empty<WorkItem>();
        return await _store.GetByIdsAsync(ids);
    }

    public async Task<IEnumerable<WorkItem>> GetDependentsAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return Array.Empty<WorkItem>();
        var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
        var all = await _server.ListAsync(opts, status: null, tags: null);
        var dependentIds = new List<Guid>();
        foreach (var candidate in all)
        {
            if (candidate.Dependencies.Contains(workItemId))
                dependentIds.Add(candidate.Id);
        }
        return await _store.GetByIdsAsync(dependentIds);
    }

    public async Task<bool> IsReadyAsync(Guid workItemId)
    {
        var ids = await GetServerDependencyIdsAsync(workItemId);
        if (ids.Count == 0) return true;

        var deps = await _store.GetByIdsAsync(ids);
        foreach (var w in deps)
        {
            if (w.Status != WorkItemStatus.Done)
                return false;
        }
        return deps.Count == ids.Count;
    }

    private async Task<IReadOnlyList<Guid>> GetServerDependencyIdsAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return Array.Empty<Guid>();
        var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
        var serverWi = await _server.GetAsync(opts, workItemId);
        return serverWi?.Dependencies?.ToList() ?? (IReadOnlyList<Guid>)Array.Empty<Guid>();
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
            var nextDeps = await GetServerDependencyIdsAsync(cur);
            foreach (var n in nextDeps) stack.Push(n);
        }
        return false;
    }

    // ──────────────────────────────────────────────────────────────────
    // PR / cleanup (engine-only fields, local sidecar)
    // ──────────────────────────────────────────────────────────────────

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

        var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        if (currentRun == null || currentRun.Status != LoopRunStatus.Running)
        {
            try
            {
                var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
                await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
                {
                    TargetStatus = RemoteWorkItemStatus.Done,
                });
            }
            catch (InvalidOperationException) { /* No remote — local only. */ }
            wi.Status = WorkItemStatus.Done;
        }

        await _store.UpdateAsync(wi);
        return true;
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

        var prev = wi.Status;
        try
        {
            var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Done,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        wi.Status = WorkItemStatus.Done;
        wi.HumanFeedbackReason = null;
        wi.CurrentLoopRunId = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        await _notifier.WorkItemStateChangedAsync(workItemId, prev, WorkItemStatus.Done);
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

        try
        {
            var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Backlog,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        wi.Status = WorkItemStatus.Backlog;
        wi.HumanFeedbackReason = null;
        wi.CurrentLoopRunId = null;
        wi.BranchName = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Human feedback
    // ──────────────────────────────────────────────────────────────────

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
                .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman
                            && n.LoopNodeId == run.CurrentNodeId)
                .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
                .FirstOrDefault()
                ?? nodes
                    .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman)
                    .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
                    .FirstOrDefault();
            if (humanRunNode != null)
            {
                humanRunNode.Status = LoopRunNodeStatus.Succeeded;
                humanRunNode.Output = input;
                humanRunNode.CompletedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunNodeAsync(humanRunNode);

                if (await IsPrNodeAsync(run, humanRunNode.LoopNodeId))
                    wi.IsPrMerged = true;
            }

            if (run.Status == LoopRunStatus.WaitingHuman)
            {
                run.Status = LoopRunStatus.Running;
                run.UpdatedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunAsync(run);
            }
        }

        await _eventLog.AppendAsync(runId, "HumanFeedbackReceived", input);

        try
        {
            var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
            await _server.AppendFeedbackAsync(opts, workItemId, input);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        wi.Status = WorkItemStatus.Running;
        wi.HumanFeedbackReason = null;
        wi.HumanFeedbackActions = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    private async Task<bool> IsPrNodeAsync(LoopRun run, Guid loopNodeId)
    {
        if (run.LoopTemplateVersionId == Guid.Empty) return false;
        var nodes = await _loopRunStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
        var node = nodes.FirstOrDefault(n => n.Id == loopNodeId);
        return node?.NodeType == NodeType.PR;
    }

    public async Task<bool> RejectHumanFeedbackAsync(Guid workItemId, string? input = null)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi == null) return false;
        if (wi.CurrentLoopRunId == null) return false;

        var run = await _loopRunStore.GetByIdAsync(wi.CurrentLoopRunId.Value);
        if (run == null) return false;

        var nodes = await _loopRunStore.GetRunNodesAsync(run.Id);
        var currentRunNode = nodes
            .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman
                        && n.LoopNodeId == run.CurrentNodeId)
            .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
            .FirstOrDefault()
            ?? nodes
                .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman)
                .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
                .FirstOrDefault();

        if (currentRunNode != null)
        {
            currentRunNode.Status = LoopRunNodeStatus.Failed;
            if (!string.IsNullOrEmpty(input))
                currentRunNode.Output = input;
            currentRunNode.CompletedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunNodeAsync(currentRunNode);
        }

        var logMessage = string.IsNullOrEmpty(input) ? "rejected by user" : $"rejected by user: {input}";
        await _eventLog.AppendAsync(run.Id, "HumanFeedbackReceived", logMessage);

        if (run.Status == LoopRunStatus.WaitingHuman)
        {
            run.Status = LoopRunStatus.Running;
            run.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(run);
        }

        try
        {
            var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
            if (!string.IsNullOrEmpty(input))
                await _server.AppendFeedbackAsync(opts, workItemId, $"rejected: {input}");
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        wi.Status = WorkItemStatus.Running;
        wi.HumanFeedbackReason = null;
        wi.HumanFeedbackActions = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<bool> SubmitHumanFeedbackRespondAsync(Guid workItemId, string input)
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
                .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman
                            && n.LoopNodeId == run.CurrentNodeId)
                .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
                .FirstOrDefault()
                ?? nodes
                    .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman)
                    .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
                    .FirstOrDefault();
            if (humanRunNode != null)
            {
                humanRunNode.Status = LoopRunNodeStatus.Responded;
                humanRunNode.Output = input;
                humanRunNode.CompletedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunNodeAsync(humanRunNode);
            }

            if (run.Status == LoopRunStatus.WaitingHuman)
            {
                run.Status = LoopRunStatus.Running;
                run.UpdatedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunAsync(run);
            }
        }

        await _eventLog.AppendAsync(runId, "HumanFeedbackReceived", input);

        try
        {
            var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
            await _server.AppendFeedbackAsync(opts, workItemId, input);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        wi.Status = WorkItemStatus.Running;
        wi.HumanFeedbackReason = null;
        wi.HumanFeedbackActions = null;
        wi.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateAsync(wi);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid workItemId)
    {
        var wi = await _store.GetByIdAsync(workItemId);
        if (wi != null)
        {
            try
            {
                var opts = await _options.ResolveForRepositoryAsync(wi.RepositoryId);
                await _server.DeleteAsync(opts, workItemId);
            }
            catch (InvalidOperationException) { /* No remote — local only. */ }
        }
        return await _store.DeleteAsync(workItemId);
    }

    // ──────────────────────────────────────────────────────────────────
    // Mapping helpers
    // ──────────────────────────────────────────────────────────────────

    internal static RemoteWorkItemStatus MapToRemote(WorkItemStatus s) => s switch
    {
        WorkItemStatus.Backlog => RemoteWorkItemStatus.Backlog,
        WorkItemStatus.WorkQueue => RemoteWorkItemStatus.WorkQueue,
        WorkItemStatus.Ready => RemoteWorkItemStatus.Ready,
        WorkItemStatus.Running => RemoteWorkItemStatus.Running,
        WorkItemStatus.HumanFeedback => RemoteWorkItemStatus.HumanFeedback,
        WorkItemStatus.WaitingForIld => RemoteWorkItemStatus.WaitingForIld,
        WorkItemStatus.Done => RemoteWorkItemStatus.Done,
        _ => RemoteWorkItemStatus.Backlog,
    };

    internal static WorkItemStatus MapFromRemote(RemoteWorkItemStatus s) => s switch
    {
        RemoteWorkItemStatus.Backlog => WorkItemStatus.Backlog,
        RemoteWorkItemStatus.WorkQueue => WorkItemStatus.WorkQueue,
        RemoteWorkItemStatus.Ready => WorkItemStatus.Ready,
        RemoteWorkItemStatus.Running => WorkItemStatus.Running,
        RemoteWorkItemStatus.HumanFeedback => WorkItemStatus.HumanFeedback,
        RemoteWorkItemStatus.WaitingForIld => WorkItemStatus.WaitingForIld,
        RemoteWorkItemStatus.Done => WorkItemStatus.Done,
        _ => WorkItemStatus.Backlog,
    };

    internal static WorkItemPriority MapFromRemote(RemoteWorkItemPriority p) => p switch
    {
        RemoteWorkItemPriority.Low => WorkItemPriority.Low,
        RemoteWorkItemPriority.Medium => WorkItemPriority.Medium,
        RemoteWorkItemPriority.High => WorkItemPriority.High,
        RemoteWorkItemPriority.Critical => WorkItemPriority.Critical,
        _ => WorkItemPriority.Medium,
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static void WriteCachedTags(WorkItem wi, IReadOnlyList<string> tags)
        => wi.TagsJson = tags.Count == 0 ? null : JsonSerializer.Serialize(tags, JsonOpts);

    internal static void WriteCachedConversation(WorkItem wi, IReadOnlyList<RemoteConversationMessage> messages)
        => wi.ConversationJson = messages.Count == 0 ? null : JsonSerializer.Serialize(messages, JsonOpts);
}
