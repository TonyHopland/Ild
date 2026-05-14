using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Interfaces;

public interface IWorkItemManager
{
    Task<string> CreateWorkItemAsync(string title, string description, Guid? repositoryId);
    Task<string> CreateWorkItemAsync(string title, string description, Guid? repositoryId, Guid? createdByLoopRunId, bool forceBacklog, IEnumerable<string>? tags = null);
    Task<bool> UpdateAsync(string workItemId, string title, string description, IEnumerable<string>? tags = null);
    Task<WorkItemView?> GetWorkItemAsync(string workItemId);

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
    Task<bool> TransitionToWorkQueueAsync(string workItemId);
    Task<bool> TransitionToReadyAsync(string workItemId);
    Task<bool> TransitionToRunningAsync(string workItemId);
    Task<bool> TransitionToHumanFeedbackAsync(string workItemId, string reason);
    Task<bool> TransitionToDoneAsync(string workItemId);

    /// <summary>
    /// Generic transition entry point. Mirrors the remote server transition contract.
    /// </summary>
    /// <param name="reason">Content stored in the server conversation thread.</param>
    /// <param name="humanFeedbackReason">Short label stored on LoopRun for frontend UI routing. Falls back to <paramref name="reason"/> when null.</param>
    Task<bool> TransitionAsync(
        string workItemId,
        RemoteWorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null,
        string? humanFeedbackReason = null);
    Task<bool> AddDependencyAsync(string workItemId, string dependsOnWorkItemId);
    Task<bool> RemoveDependencyAsync(string workItemId, string dependsOnWorkItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependenciesAsync(string workItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependentsAsync(string workItemId);
    Task<bool> IsReadyAsync(string workItemId);
    Task<bool> LinkPullRequestAsync(string workItemId, string prUrl);
    Task<bool> ManuallyMarkMergedAsync(string workItemId);
    Task<bool> CleanupToDoneAsync(string workItemId);
    Task<bool> CleanupToBacklogAsync(string workItemId);
    Task<bool> SubmitHumanFeedbackInputAsync(string workItemId, string input);
    Task<bool> SubmitHumanFeedbackRespondAsync(string workItemId, string input);
    Task<bool> RejectHumanFeedbackAsync(string workItemId, string? input = null);
    Task<bool> DeleteAsync(string workItemId);
}
