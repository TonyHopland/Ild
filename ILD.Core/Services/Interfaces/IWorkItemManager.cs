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
    /// <param name="name">Optional author display name for the conversation entry (e.g. the originating node's title).</param>
    Task<bool> TransitionAsync(
        string workItemId,
        RemoteWorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null,
        string? humanFeedbackReason = null,
        string? name = null);

    /// <summary>
    /// Append an AI-authored conversation turn (e.g. an AI node's output) to the
    /// work item's thread without changing its status. <paramref name="name"/> is
    /// the author label shown in the UI, typically the node's title.
    /// </summary>
    Task<bool> AppendAiTurnAsync(string workItemId, string name, string content);
    Task<bool> AddDependencyAsync(string workItemId, string dependsOnWorkItemId);
    Task<bool> RemoveDependencyAsync(string workItemId, string dependsOnWorkItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependenciesAsync(string workItemId);
    Task<IReadOnlyList<WorkItemView>> GetDependentsAsync(string workItemId);
    Task<bool> IsReadyAsync(string workItemId);
    Task<bool> LinkPullRequestAsync(string workItemId, string prUrl);
    Task<bool> ManuallyMarkMergedAsync(string workItemId);

    /// <summary>
    /// Mark the work item's PR merged and advance the run past its parked PR
    /// node: signals the PR (or any waiting/failed) node to success so the
    /// engine routes onward, or — when the run is unrecoverable because the
    /// node's template entry was removed — transitions the work item to Done
    /// directly. Returns false when the work item has no current run.
    /// </summary>
    Task<bool> MarkMergedAndAdvanceAsync(string workItemId);
    Task<bool> CleanupToDoneAsync(string workItemId);
    Task<bool> CleanupToBacklogAsync(string workItemId);

    /// <summary>
    /// Commit any uncommitted changes in the work item's current run worktree
    /// and push its branch to origin, using the same built-in repository
    /// functionality the PR node uses. Lets a human keep work produced by a
    /// loop that has no PR node. Returns the pushed branch name on success, or
    /// an error message describing why the push could not happen.
    /// </summary>
    Task<(bool Success, string? Branch, string? Error)> CommitAndPushBranchAsync(string workItemId);
    Task<bool> SubmitHumanFeedbackInputAsync(string workItemId, string input);
    Task<bool> SubmitHumanFeedbackRespondAsync(string workItemId, string input);
    Task<bool> RejectHumanFeedbackAsync(string workItemId, string? input = null);
    Task<bool> DeleteAsync(string workItemId);
}
