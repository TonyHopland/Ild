using ILD.Core.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class WorkItemManager : IWorkItemManager
{
    private readonly ILogger<WorkItemManager> _logger;
    private readonly AppDbContext _dbContext;

    public WorkItemManager(ILogger<WorkItemManager> logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public Task<Guid> CreateWorkItemAsync(string title, string description, Guid? loopTemplateId, Guid? repositoryId)
    {
        throw new NotImplementedException(nameof(CreateWorkItemAsync));
    }

    public Task<WorkItem?> GetWorkItemAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(GetWorkItemAsync));
    }

    public Task<IEnumerable<WorkItem>> GetWorkItemsByStatusAsync(WorkItemStatus status)
    {
        throw new NotImplementedException(nameof(GetWorkItemsByStatusAsync));
    }

    public Task<bool> TransitionToWorkQueueAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(TransitionToWorkQueueAsync));
    }

    public Task<bool> TransitionToReadyAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(TransitionToReadyAsync));
    }

    public Task<bool> TransitionToRunningAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(TransitionToRunningAsync));
    }

    public Task<bool> TransitionToHumanFeedbackAsync(Guid workItemId, string reason)
    {
        throw new NotImplementedException(nameof(TransitionToHumanFeedbackAsync));
    }

    public Task<bool> TransitionToDoneAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(TransitionToDoneAsync));
    }

    public Task<bool> AddDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId)
    {
        throw new NotImplementedException(nameof(AddDependencyAsync));
    }

    public Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId)
    {
        throw new NotImplementedException(nameof(RemoveDependencyAsync));
    }

    public Task<IEnumerable<WorkItem>> GetDependenciesAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(GetDependenciesAsync));
    }

    public Task<IEnumerable<WorkItem>> GetDependentsAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(GetDependentsAsync));
    }

    public Task<bool> IsReadyAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(IsReadyAsync));
    }

    public Task<bool> LinkPullRequestAsync(Guid workItemId, string prUrl)
    {
        throw new NotImplementedException(nameof(LinkPullRequestAsync));
    }

    public Task<bool> ManuallyMarkMergedAsync(Guid workItemId)
    {
        throw new NotImplementedException(nameof(ManuallyMarkMergedAsync));
    }
}
