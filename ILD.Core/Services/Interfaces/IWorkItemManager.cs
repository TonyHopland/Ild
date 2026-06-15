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

    /// <summary>
    /// Route the parked node to its named custom edge <paramref name="edgeName"/>
    /// (a Human node button), passing <paramref name="input"/> as the node's
    /// output for downstream <c>{{PreviousNode.Output}}</c>.
    /// </summary>
    Task<bool> SubmitHumanFeedbackEdgeAsync(string workItemId, string edgeName, string input);
    Task<bool> RejectHumanFeedbackAsync(string workItemId, string? input = null);

    /// <summary>
    /// Merge the pull request linked to the work item's current run on the
    /// remote provider and, when <paramref name="deleteBranch"/> is set, delete
    /// the source branch afterwards (best effort). On a successful merge the
    /// loop is advanced along the <c>OnSuccess</c> edge — the same continuation
    /// the Approve action uses. A failed merge leaves the work item parked and
    /// does not advance the loop. Returns <c>null</c> when the work item or its
    /// current run cannot be found.
    /// </summary>
    Task<MergePullRequestResult?> MergePullRequestAsync(string workItemId, bool deleteBranch);
    Task<bool> DeleteAsync(string workItemId);
}

/// <summary>
/// Outcome of a <see cref="IWorkItemManager.MergePullRequestAsync"/> call.
/// <paramref name="Merged"/> reports whether the remote merge succeeded;
/// <paramref name="Error"/> carries the reason when it did not. When branch
/// deletion was requested, <paramref name="BranchDeleted"/> says whether it
/// succeeded and <paramref name="BranchWarning"/> describes a best-effort
/// delete failure that did not block the merge.
/// </summary>
public sealed record MergePullRequestResult(
    bool Merged,
    string? Error,
    bool BranchDeleted,
    string? BranchWarning);
