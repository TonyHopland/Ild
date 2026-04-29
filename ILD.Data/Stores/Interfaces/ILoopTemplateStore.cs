using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface ILoopTemplateStore
{
    Task<LoopTemplate?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<LoopTemplate>> GetAllAsync();
    Task<LoopTemplateVersion?> GetLatestVersionAsync(Guid templateId);
    Task<LoopTemplateVersion?> GetVersionByIdAsync(Guid versionId);
    Task<LoopTemplateVersion?> GetVersionAsync(Guid templateId, int versionNumber);
    Task<IReadOnlyList<LoopTemplateVersion>> GetVersionsAsync(Guid templateId);
    Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId);
    Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForVersionAsync(Guid versionId);
    Task<int> GetNextVersionNumberAsync(Guid templateId);
    Task CreateTemplateAsync(LoopTemplate template);
    Task UpdateTemplateAsync(LoopTemplate template);
    Task DeleteTemplateAsync(LoopTemplate template);
    Task CreateVersionAsync(LoopTemplateVersion version);
    Task CreateNodesAsync(IReadOnlyList<LoopNode> nodes);
    Task CreateEdgesAsync(IReadOnlyList<LoopNodeEdge> edges);
    Task DeleteNodesForVersionAsync(Guid versionId);
    Task DeleteEdgesForVersionAsync(Guid versionId);
    Task DeleteVersionsForTemplateAsync(Guid templateId);
}
