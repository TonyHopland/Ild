using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public record TemplateGraph(
    LoopTemplate Template,
    LoopTemplateVersion Version,
    IReadOnlyList<LoopNode> Nodes,
    IReadOnlyList<LoopNodeEdge> Edges);

public interface ILoopTemplateStore
{
    Task<LoopTemplate?> GetByIdAsync(Guid id);
    Task<LoopTemplate?> GetByVersionIdAsync(Guid versionId);
    Task<TemplateGraph?> GetTemplateGraphByVersionIdAsync(Guid versionId);
    Task<IReadOnlyList<LoopTemplate>> GetAllAsync(int skip = 0, int take = 100, bool includeArchived = false);
    Task<LoopTemplateVersion?> GetLatestVersionAsync(Guid templateId);
    Task<LoopTemplateVersion?> GetVersionByIdAsync(Guid versionId);
    Task<LoopTemplateVersion?> GetVersionAsync(Guid templateId, int versionNumber);
    Task<IReadOnlyList<LoopTemplateVersion>> GetVersionsAsync(Guid templateId);
    Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId);
    Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForVersionAsync(Guid versionId);
    Task<int> GetNextVersionNumberAsync(Guid templateId);
    Task CreateTemplateAsync(LoopTemplate template);
    Task UpdateTemplateAsync(LoopTemplate template);
    Task SetArchivedAsync(Guid templateId, bool archived);
    Task DeleteTemplateAsync(LoopTemplate template);
    Task CreateVersionAsync(LoopTemplateVersion version);
    Task CreateNodesAsync(IReadOnlyList<LoopNode> nodes);
    Task CreateEdgesAsync(IReadOnlyList<LoopNodeEdge> edges);
    Task DeleteNodesForVersionAsync(Guid versionId);
    Task DeleteEdgesForVersionAsync(Guid versionId);
    Task DeleteVersionsForTemplateAsync(Guid templateId);
    Task SaveChangesAsync();
}
