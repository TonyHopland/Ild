using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IWorkItemManager
{
    Task<Guid> CreateWorkItemAsync(string title, string description, Guid? loopTemplateId, Guid? repositoryId);
    Task<Guid> CreateWorkItemAsync(string title, string description, Guid? loopTemplateId, Guid? repositoryId, Guid? createdByLoopRunId, bool forceBacklog, IEnumerable<string>? tags = null);
    Task<bool> UpdateAsync(Guid workItemId, string title, string description, Guid? loopTemplateId = null);
    Task<WorkItem?> GetWorkItemAsync(Guid workItemId);

    /// <summary>
    /// Server-authoritative listing for the work-item domain. Always
    /// queries the WorkItemServer for the items it knows about and joins
    /// them with this ILD instance's local sidecars (which carry engine-
    /// only fields like RepositoryId / WorktreePath / CurrentLoopRunId).
    /// Items that exist on the server but have no sidecar here are
    /// skipped — they belong to another ILD instance. If the server is
    /// unreachable the underlying HTTP exception is surfaced so the UI
    /// fails loudly instead of showing stale cached rows.
    /// </summary>
    Task<IReadOnlyList<WorkItem>> ListAsync(
        WorkItemStatus? status,
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
    /// Generic, permissive transition entry point used as the canonical
    /// surface for every work item state mutation. Mirrors the eventual
    /// remote server transition contract:
    /// - Always succeeds when the work item exists (returns false only if
    ///   the work item is missing). Transitioning to the current status is
    ///   a no-op but still returns true.
    /// - When transitioning to <see cref="WorkItemStatus.HumanFeedback"/>,
    ///   the supplied <paramref name="reason"/>/<paramref name="actions"/>
    ///   are stored on the work item (null leaves the existing value).
    /// - When transitioning to any other status, HumanFeedbackReason and
    ///   HumanFeedbackActions are cleared.
    /// - <paramref name="currentLoopRunId"/> is treated as a sentinel: null
    ///   leaves the existing CurrentLoopRunId, <see cref="Guid.Empty"/>
    ///   clears it, any other value sets it.
    /// </summary>
    Task<bool> TransitionAsync(
        Guid workItemId,
        WorkItemStatus targetStatus,
        string? reason = null,
        string? actions = null,
        Guid? currentLoopRunId = null);
    Task<bool> AddDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId);
    Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId);
    Task<IEnumerable<WorkItem>> GetDependenciesAsync(Guid workItemId);
    Task<IEnumerable<WorkItem>> GetDependentsAsync(Guid workItemId);
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
