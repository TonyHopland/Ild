using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface IWorkItemStore
{
    Task<WorkItem?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<WorkItem>> GetByStatusAsync(WorkItemStatus status);
    Task<IReadOnlyList<WorkItem>> GetByIdsAsync(IReadOnlyList<Guid> ids);
    Task CreateAsync(WorkItem workItem);
    Task UpdateAsync(WorkItem workItem);
    Task<LoopTemplateVersion?> GetLatestTemplateVersionAsync(Guid templateId);
    Task<Repository?> GetRepositoryAsync(Guid id);
    Task<bool> DeleteAsync(Guid id);
}
