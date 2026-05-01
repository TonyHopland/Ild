using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface IWorkItemStore
{
    Task<WorkItem?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<WorkItem>> GetByStatusAsync(WorkItemStatus status);
    Task<IReadOnlyList<WorkItem>> GetByRepositoryAsync(Guid repositoryId);
    Task<IReadOnlyList<WorkItem>> GetByRepositoryIdsAsync(IReadOnlyList<Guid> repositoryIds);
    Task<IReadOnlyList<WorkItem>> GetByIdsAsync(IReadOnlyList<Guid> ids);
    Task CreateAsync(WorkItem workItem);
    Task UpdateAsync(WorkItem workItem);
    Task<bool> AddDependencyAsync(Guid workItemId, Guid dependencyWorkItemId);
    Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependencyWorkItemId);
    Task<IReadOnlyList<Guid>> GetDependencyIdsAsync(Guid workItemId);
    Task<IReadOnlyList<Guid>> GetDependentIdsAsync(Guid workItemId);
    Task<bool> HasRunningRunAsync(Guid workItemId);
    Task<LoopTemplateVersion?> GetLatestTemplateVersionAsync(Guid templateId);
    Task<Repository?> GetRepositoryAsync(Guid id);
}
