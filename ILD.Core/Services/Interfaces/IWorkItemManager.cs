using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Interfaces;

public interface IWorkItemManager
{
    Task<Guid> CreateWorkItemAsync(string title, string description, Guid? repositoryId);
    Task<Guid> CreateWorkItemAsync(string title, string description, Guid? repositoryId, Guid? createdByLoopRunId, bool forceBacklog, IEnumerable<string>? tags = null);
    Task<bool> UpdateAsync(Guid workItemId, string title, string description, IEnumerable<string>? tags = null);
    Task<WorkItemView?> GetWorkItemAsync(Guid workItemId);

    /// <summary>
    /// Server-authoritative listing. Queries the WorkItemServer and merges
    /// engine-only fields from LoopRun rows.
    /// </summary>
    Task<IReadOnlyList<WorkItemView>> ListAsync(
        RemoteWorkItemStatus? status,
        Guid? createdByLoopRunId,
        Guid? repositoryId,
        int skip,
        int take);
    Task<bool> TransitionToWorkQueueAsync(Guid workItemId);
    Task<bool> TransitionToReadyAsync(Guid workItemId);
    Task<bool> TransitionToRunningAsync(Guid workItemId);
    Task<bool> TransitionToHumanFeedbackAsync(Guid workItemId, string reason);
    Task<bool> TransitionToDoneAsync(Guid workItemId);

    /// <summary>
    /// Generic transition entry point. Mirrors the remote server transition contract.
    /// </summary>
    /// <param name="reason">Content stored in the server conversation thread.</param>
    /// <param name="humanFeedbackReason">Short label stored on LoopRun for frontend UI routing. Falls back to <paramref name="reason"/> when null.</param>
    Task<bool> TransitionAsync(
        Guid workItemId,
        RemoteWorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null,
        string? humanFeedbackReason = null);
    Task<bool> AddDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId);
    Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependenciesAsync(Guid workItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependentsAsync(Guid workItemId);
    Task<bool> IsReadyAsync(Guid workItemId);
    Task<bool> LinkPullRequestAsync(Guid workItemId, string prUrl);
    Task<bool> ManuallyMarkMergedAsync(Guid workItemId);
    Task<bool> CleanupToDoneAsync(Guid workItemId);
    Task<bool> CleanupToBacklogAsync(Guid workItemId);
    Task<bool> SubmitHumanFeedbackInputAsync(Guid workItemId, string input);
    Task<bool> SubmitHumanFeedbackRespondAsync(Guid workItemId, string input);
    Task<bool> RejectHumanFeedbackAsync(Guid workItemId, string? input = null);
    Task<bool> DeleteAsync(Guid workItemId);
}
