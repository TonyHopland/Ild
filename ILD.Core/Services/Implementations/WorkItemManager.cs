using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using System.Text.Json;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Remote-backed implementation of <see cref="IWorkItemManager"/>.
/// The WorkItemServer is authoritative for the work-item domain.
/// Engine-only fields (worktree, branch, PR, current loop run) live on LoopRun.
/// </summary>
public class WorkItemManager : IWorkItemManager
{
    private readonly IRepositoryManager _repoManager;
    private readonly IProviderStore _providerStore;
    private readonly IEventLogService _eventLog;
    private readonly ILoopRunStore _loopRunStore;
    private readonly IWorkItemNotifier _notifier;
    private readonly IWorkItemServerClient _server;
    private readonly IWorkItemServerOptionsResolver _options;
    private readonly IWorktreePreviewService _previewService;

    public WorkItemManager(
        IRepositoryManager repoManager,
        IProviderStore providerStore,
        IEventLogService eventLog,
        ILoopRunStore loopRunStore,
        IWorkItemServerClient server,
        IWorkItemServerOptionsResolver options,
        IWorkItemNotifier? notifier = null,
        IWorktreePreviewService? previewService = null)
    {
        _repoManager = repoManager;
        _providerStore = providerStore;
        _eventLog = eventLog;
        _loopRunStore = loopRunStore;
        _server = server;
        _options = options;
        _notifier = notifier ?? new NoopWorkItemNotifier();
        _previewService = previewService ?? new NoopPreviewService();
    }

    // ──────────────────────────────────────────────────────────────────
    // Create / Read / Update
    // ──────────────────────────────────────────────────────────────────

    public Task<string> CreateWorkItemAsync(string title, string description, Guid? repositoryId)
        => CreateWorkItemAsync(title, description, repositoryId, null, false);

    public async Task<string> CreateWorkItemAsync(
        string title,
        string description,
        Guid? repositoryId,
        Guid? createdByLoopRunId,
        bool forceBacklog,
        IEnumerable<string>? tags = null)
    {
        var opts = await _options.ResolveForRepositoryAsync(repositoryId);

        RemoteWorkItemStatus? forceStatus = forceBacklog ? RemoteWorkItemStatus.Backlog : null;
        if (!forceBacklog && repositoryId.HasValue)
        {
            var repo = await _providerStore.GetRepositoryByIdAsync(repositoryId.Value)
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
            CreatedByLoopRunId = createdByLoopRunId,
            RepositoryId = repositoryId,
        });

        return serverWi.Id;
    }

    public async Task<WorkItemView?> GetWorkItemAsync(string workItemId)
    {
        var remote = await _server.GetAsync(await _options.ResolveForWorkItemAsync(workItemId), workItemId);
        if (remote == null) return null;

        var runs = await _loopRunStore.GetAllByWorkItemAsync(workItemId);
        var currentRun = runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
                       ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.WaitingHuman)
                       ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.Failed)
                       ?? runs.FirstOrDefault(r => r.Status == LoopRunStatus.Cancelled)
                       ?? runs.Where(r => r.Status != LoopRunStatus.Completed)
                              .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
                              .FirstOrDefault();

        return BuildView(remote, currentRun, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty));
    }

    public async Task<IReadOnlyList<WorkItemView>> ListAsync(
        RemoteWorkItemStatus? status,
        Guid? createdByLoopRunId,
        Guid? repositoryId,
        int skip,
        int take)
    {
        if (skip < 0) skip = 0;
        if (take <= 0) take = 100;

        var opts = await _options.ResolveForRepositoryAsync(repositoryId);
        var remoteList = await _server.ListAsync(opts, status, tags: null);
        if (remoteList.Count == 0) return Array.Empty<WorkItemView>();

        var allRuns = await _loopRunStore.GetAllAsync(skip: 0, take: int.MaxValue);
        var runsByWorkItem = allRuns.GroupBy(r => r.WorkItemId).ToDictionary(g => g.Key, g => g.ToList());

        var views = new List<WorkItemView>();
        foreach (var r in remoteList)
        {
            LoopRun? currentRun = null;
            if (runsByWorkItem.TryGetValue(r.Id, out var runs))
            {
                currentRun = runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.Running)
                           ?? runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.WaitingHuman)
                           ?? runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.Failed)
                           ?? runs.FirstOrDefault(rn => rn.Status == LoopRunStatus.Cancelled)
                           ?? runs.Where(rn => rn.Status != LoopRunStatus.Completed)
                                  .OrderByDescending(rn => rn.StartedAt ?? rn.CreatedAt)
                                  .FirstOrDefault();
            }
            var view = BuildView(r, currentRun, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty));
            if (createdByLoopRunId.HasValue && view.CreatedByLoopRunId != createdByLoopRunId) continue;
            if (repositoryId.HasValue && view.RepositoryId != repositoryId) continue;
            views.Add(view);
        }

        return views
            .OrderByDescending(v => v.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToList();
    }

    private static WorkItemView BuildView(RemoteWorkItem remote, LoopRun? run, bool isPreviewRunning)
    {
        return new WorkItemView
        {
            Id = remote.Id,
            Title = remote.Title,
            Description = remote.Description,
            CreatedBy = remote.CreatedBy,
            CreatedAt = remote.CreatedAt,
            UpdatedAt = remote.UpdatedAt,
            Priority = remote.Priority,
            Status = remote.Status,
            Tags = remote.Tags,
            Conversation = remote.Conversation,
            HumanFeedbackActions = remote.HumanFeedbackActions,
            RepositoryId = run?.RepositoryId ?? remote.RepositoryId,
            CreatedByLoopRunId = run?.CreatedByLoopRunId ?? remote.CreatedByLoopRunId,
            WorktreePath = run?.WorktreePath,
            BranchName = run?.BranchName,
            PrUrl = run?.PrUrl,
            IsPrMerged = run?.IsPrMerged == true,
            HumanFeedbackReason = run?.HumanFeedbackReason,
            CurrentLoopRunId = run?.Id,
            IsPreviewRunning = isPreviewRunning,
        };
    }

    public async Task<bool> UpdateAsync(string workItemId, string title, string description, IEnumerable<string>? tags = null)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var updated = await _server.UpdateAsync(opts, workItemId, new RemoteUpdateWorkItemRequest
        {
            Title = title,
            Description = description,
            Tags = tags?.ToList(),
        });
        return updated != null;
    }

    // ──────────────────────────────────────────────────────────────────
    // Transitions
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> TransitionToWorkQueueAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;
        if (wi.Status != RemoteWorkItemStatus.Backlog && wi.Status != RemoteWorkItemStatus.HumanFeedback)
            return false;

        await TransitionAsync(workItemId, RemoteWorkItemStatus.WorkQueue);

        if (await IsReadyAsync(workItemId))
            await TransitionToReadyAsync(workItemId);
        return true;
    }

    public async Task<bool> TransitionToReadyAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;
        if (!await IsReadyAsync(workItemId)) return false;
        if (wi.Status != RemoteWorkItemStatus.WorkQueue && wi.Status != RemoteWorkItemStatus.Backlog)
            return false;
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.Ready);
    }

    public async Task<bool> TransitionToRunningAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;
        if (wi.Status != RemoteWorkItemStatus.Ready && wi.Status != RemoteWorkItemStatus.HumanFeedback)
            return false;
        return await TransitionAsync(workItemId, RemoteWorkItemStatus.Running);
    }

    public Task<bool> TransitionToHumanFeedbackAsync(string workItemId, string reason)
        => TransitionAsync(workItemId, RemoteWorkItemStatus.HumanFeedback, reason);

    public Task<bool> TransitionToDoneAsync(string workItemId)
        => TransitionAsync(workItemId, RemoteWorkItemStatus.Done);

    public async Task<bool> TransitionAsync(
        string workItemId,
        RemoteWorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null,
        string? humanFeedbackReason = null)
    {
        var prevWi = await GetWorkItemAsync(workItemId);
        if (prevWi == null) return false;

        var prev = prevWi.Status;
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var resp = await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
        {
            TargetStatus = targetStatus,
            Reason = reason,
            Actions = actions,
        });

        if (!resp.Success)
            return false;

        var actual = resp.ActualStatus;

        // Update engine-only fields on the current LoopRun
        Guid? effectiveRunId = currentLoopRunId;
        if (!effectiveRunId.HasValue || effectiveRunId.Value == Guid.Empty)
        {
            var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
            effectiveRunId = currentRun?.Id;
        }
        if (effectiveRunId.HasValue)
        {
            var run = await _loopRunStore.GetByIdAsync(effectiveRunId.Value);
            if (run != null)
            {
                if (actual == RemoteWorkItemStatus.HumanFeedback && reason != null)
                {
                    // Use the dedicated humanFeedbackReason for UI routing on
                    // the LoopRun. Falls back to reason when not supplied.
                    run.HumanFeedbackReason = humanFeedbackReason ?? reason;
                }
                else
                    run.HumanFeedbackReason = null;
                run.UpdatedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunAsync(run);
            }
        }

        if (prev != actual)
            await _notifier.WorkItemStateChangedAsync(workItemId, prev, actual);

        if (actual == RemoteWorkItemStatus.HumanFeedback && reason != null)
            await _notifier.HumanFeedbackRequiredAsync(workItemId, reason);

        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Dependencies (server-only)
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> AddDependencyAsync(string workItemId, string dependsOnWorkItemId)
    {
        if (workItemId == dependsOnWorkItemId)
            throw new InvalidOperationException("A work item cannot depend on itself.");

        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;

        if (await WouldCreateCycle(workItemId, dependsOnWorkItemId))
            throw new InvalidOperationException("Adding this dependency would create a cycle.");

        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        return await _server.AddDependencyAsync(opts, workItemId, dependsOnWorkItemId);
    }

    public async Task<bool> RemoveDependencyAsync(string workItemId, string dependsOnWorkItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        return await _server.RemoveDependencyAsync(opts, workItemId, dependsOnWorkItemId);
    }

    public async Task<IReadOnlyList<WorkItemView>> GetDependenciesAsync(string workItemId)
    {
        var ids = await GetServerDependencyIdsAsync(workItemId);
        if (ids.Count == 0) return Array.Empty<WorkItemView>();

        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var views = new List<WorkItemView>();
        foreach (var id in ids)
        {
            var remote = await _server.GetAsync(opts, id);
            if (remote != null)
            {
                var runs = await _loopRunStore.GetAllByWorkItemAsync(id);
                var currentRun = runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
                               ?? runs.OrderByDescending(r => r.StartedAt ?? r.CreatedAt).FirstOrDefault();
                views.Add(BuildView(remote, currentRun, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty)));
            }
        }
        return views;
    }

    public async Task<IReadOnlyList<WorkItemView>> GetDependentsAsync(string workItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var all = await _server.ListAsync(opts, status: null, tags: null);
        var views = new List<WorkItemView>();
        foreach (var candidate in all)
        {
            if (candidate.Dependencies.Contains(workItemId))
            {
                var runs = await _loopRunStore.GetAllByWorkItemAsync(candidate.Id);
                var currentRun = runs.FirstOrDefault(r => r.Status == LoopRunStatus.Running)
                               ?? runs.OrderByDescending(r => r.StartedAt ?? r.CreatedAt).FirstOrDefault();
                views.Add(BuildView(candidate, currentRun, _previewService.IsPreviewRunning(currentRun?.WorktreePath ?? string.Empty)));
            }
        }
        return views;
    }

    public async Task<bool> IsReadyAsync(string workItemId)
    {
        var ids = await GetServerDependencyIdsAsync(workItemId);
        if (ids.Count == 0) return true;

        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        foreach (var depId in ids)
        {
            var dep = await _server.GetAsync(opts, depId);
            if (dep == null || dep.Status != RemoteWorkItemStatus.Done)
                return false;
        }
        return true;
    }

    private async Task<IReadOnlyList<string>> GetServerDependencyIdsAsync(string workItemId)
    {
        var opts = await _options.ResolveForWorkItemAsync(workItemId);
        var serverWi = await _server.GetAsync(opts, workItemId);
        return serverWi?.Dependencies?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private async Task<bool> WouldCreateCycle(string workItemId, string newDepId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
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
    // PR / cleanup (engine-only fields on LoopRun)
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> LinkPullRequestAsync(string workItemId, string prUrl)
    {
        var run = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        if (run == null) return false;
        run.PrUrl = prUrl;
        run.UpdatedAt = DateTime.UtcNow;
        await _loopRunStore.UpdateRunAsync(run);
        return true;
    }

    public async Task<bool> ManuallyMarkMergedAsync(string workItemId)
    {
        var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        if (currentRun == null) return false;

        currentRun.IsPrMerged = true;
        currentRun.UpdatedAt = DateTime.UtcNow;

        if (currentRun.Status != LoopRunStatus.Running)
        {
            try
            {
                var opts = await _options.ResolveForWorkItemAsync(workItemId);
                await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
                {
                    TargetStatus = RemoteWorkItemStatus.Done,
                });
            }
            catch (InvalidOperationException) { /* No remote — local only. */ }
        }

        await _loopRunStore.UpdateRunAsync(currentRun);
        return true;
    }

    public async Task<bool> CleanupToDoneAsync(string workItemId)
    {
        var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;

        if (!string.IsNullOrEmpty(currentRun?.WorktreePath))
        {
            await _repoManager.DestroyWorktreeAsync(currentRun.WorktreePath);
            currentRun.WorktreePath = null;
            await _loopRunStore.UpdateRunAsync(currentRun);
        }

        var prev = wi.Status;
        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Done,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (currentRun != null)
        {
            currentRun.HumanFeedbackReason = null;
            currentRun.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(currentRun);
        }

        await _notifier.WorkItemStateChangedAsync(workItemId, prev, RemoteWorkItemStatus.Done);
        return true;
    }

    public async Task<bool> CleanupToBacklogAsync(string workItemId)
    {
        var currentRun = await _loopRunStore.GetCurrentByWorkItemAsync(workItemId);

        if (currentRun != null && !string.IsNullOrEmpty(currentRun.WorktreePath))
        {
            await _repoManager.DestroyWorktreeAsync(currentRun.WorktreePath);
            currentRun.WorktreePath = null;
        }

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Backlog,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (currentRun != null)
        {
            currentRun.Status = LoopRunStatus.Completed;
            currentRun.HumanFeedbackReason = null;
            currentRun.BranchName = null;
            currentRun.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(currentRun);
        }

        return true;
    }

    // ──────────────────────────────────────────────────────────────────
    // Human feedback
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> SubmitHumanFeedbackInputAsync(string workItemId, string input)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null || wi.CurrentLoopRunId == null) return false;

        var runId = wi.CurrentLoopRunId.Value;
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run != null)
        {
            var nodes = await _loopRunStore.GetRunNodesAsync(runId);
            var humanRunNode = FindWaitingHumanNode(nodes, run.CurrentNodeId);
            if (humanRunNode != null)
            {
                humanRunNode.Status = LoopRunNodeStatus.Succeeded;
                humanRunNode.Output = input;
                humanRunNode.CompletedAt = DateTime.UtcNow;
                await _loopRunStore.UpdateRunNodeAsync(humanRunNode);

                if (await IsPrNodeAsync(run, humanRunNode.LoopNodeId))
                    run.IsPrMerged = true;
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
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.AppendFeedbackAsync(opts, workItemId, input);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (run != null)
        {
            run.HumanFeedbackReason = null;
            run.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(run);
        }
        return true;
    }

    private static LoopRunNode? FindWaitingHumanNode(IReadOnlyList<LoopRunNode> nodes, Guid? currentNodeId)
    {
        var primary = nodes
            .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman && n.LoopNodeId == currentNodeId)
            .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
            .FirstOrDefault();
        if (primary != null) return primary;
        return nodes
            .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman)
            .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private async Task<bool> IsPrNodeAsync(LoopRun run, Guid loopNodeId)
    {
        if (run.LoopTemplateVersionId == Guid.Empty) return false;
        var nodes = await _loopRunStore.GetNodesForVersionAsync(run.LoopTemplateVersionId);
        var node = nodes.FirstOrDefault(n => n.Id == loopNodeId);
        return node?.NodeType == NodeType.PR;
    }

    public async Task<bool> RejectHumanFeedbackAsync(string workItemId, string? input = null)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null || wi.CurrentLoopRunId == null) return false;

        var run = await _loopRunStore.GetByIdAsync(wi.CurrentLoopRunId.Value);
        if (run == null) return false;

        var nodes = await _loopRunStore.GetRunNodesAsync(run.Id);
        var currentRunNode = FindWaitingHumanNode(nodes, run.CurrentNodeId);

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
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            if (!string.IsNullOrEmpty(input))
                await _server.AppendFeedbackAsync(opts, workItemId, $"rejected: {input}");
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        run.HumanFeedbackReason = null;
        run.UpdatedAt = DateTime.UtcNow;
        await _loopRunStore.UpdateRunAsync(run);
        return true;
    }

    public async Task<bool> SubmitHumanFeedbackRespondAsync(string workItemId, string input)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null || wi.CurrentLoopRunId == null) return false;

        var runId = wi.CurrentLoopRunId.Value;
        var run = await _loopRunStore.GetByIdAsync(runId);
        if (run != null)
        {
            var nodes = await _loopRunStore.GetRunNodesAsync(runId);
            var humanRunNode = FindWaitingHumanNode(nodes, run.CurrentNodeId);
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
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.AppendFeedbackAsync(opts, workItemId, input);
            await _server.TransitionAsync(opts, workItemId, new RemoteTransitionRequest
            {
                TargetStatus = RemoteWorkItemStatus.Running,
            });
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        if (run != null)
        {
            run.HumanFeedbackReason = null;
            run.UpdatedAt = DateTime.UtcNow;
            await _loopRunStore.UpdateRunAsync(run);
        }
        return true;
    }

    public async Task<bool> DeleteAsync(string workItemId)
    {
        var wi = await GetWorkItemAsync(workItemId);
        if (wi == null) return false;

        try
        {
            var opts = await _options.ResolveForWorkItemAsync(workItemId);
            await _server.DeleteAsync(opts, workItemId);
        }
        catch (InvalidOperationException) { /* No remote — local only. */ }

        // Delete all LoopRuns for this work item
        var runs = await _loopRunStore.GetAllByWorkItemAsync(workItemId);
        foreach (var run in runs)
        {
            await _loopRunStore.DeleteAsync(run.Id);
        }
        return true;
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
}
