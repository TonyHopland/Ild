using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

public interface IWorkItemManager
{
    Task<Guid> CreateWorkItemAsync(string title, string description, Guid? loopTemplateId, Guid? repositoryId);
    Task<WorkItem?> GetWorkItemAsync(Guid workItemId);
    Task<IEnumerable<WorkItem>> GetWorkItemsByStatusAsync(WorkItemStatus status);
    Task<bool> TransitionToWorkQueueAsync(Guid workItemId);
    Task<bool> TransitionToReadyAsync(Guid workItemId);
    Task<bool> TransitionToRunningAsync(Guid workItemId);
    Task<bool> TransitionToHumanFeedbackAsync(Guid workItemId, string reason);
    Task<bool> TransitionToDoneAsync(Guid workItemId);
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
    Task<bool> RejectHumanFeedbackAsync(Guid workItemId);
}
