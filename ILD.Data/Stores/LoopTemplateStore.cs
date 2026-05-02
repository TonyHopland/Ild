using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public class LoopTemplateStore : ILoopTemplateStore
{
    private readonly AppDbContext _db;

    public LoopTemplateStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<LoopTemplate?> GetByIdAsync(Guid id)
        => await _db.LoopTemplates.FindAsync(id).AsTask();

    public async Task<LoopTemplate?> GetByVersionIdAsync(Guid versionId)
        => await _db.LoopTemplates
            .FirstOrDefaultAsync(t => t.Versions.Any(v => v.Id == versionId));

    public async Task<TemplateGraph?> GetTemplateGraphByVersionIdAsync(Guid versionId)
    {
        var version = await _db.LoopTemplateVersions
            .AsNoTracking()
            .Include(v => v.LoopTemplate)
            .Include(v => v.Nodes)
                .ThenInclude(n => n.OutgoingEdges)
            .FirstOrDefaultAsync(v => v.Id == versionId);
        if (version == null) return null;
        var nodes = version.Nodes.ToList();
        var edges = nodes.SelectMany(n => n.OutgoingEdges).Distinct().ToList();
        return new TemplateGraph(version.LoopTemplate, version, nodes, edges);
    }

    public async Task<IReadOnlyList<LoopTemplate>> GetAllAsync(int skip = 0, int take = 100)
        => await _db.LoopTemplates.OrderBy(t => t.Name).Skip(skip).Take(take).ToListAsync();

    public async Task<LoopTemplateVersion?> GetVersionByIdAsync(Guid versionId)
        => await _db.LoopTemplateVersions.FindAsync(versionId).AsTask();

    public async Task<LoopTemplateVersion?> GetLatestVersionAsync(Guid templateId)
        => await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync();

    public async Task<LoopTemplateVersion?> GetVersionAsync(Guid templateId, int versionNumber)
        => await _db.LoopTemplateVersions
            .FirstOrDefaultAsync(v => v.LoopTemplateId == templateId && v.VersionNumber == versionNumber);

    public async Task<IReadOnlyList<LoopTemplateVersion>> GetVersionsAsync(Guid templateId)
        => await _db.LoopTemplateVersions.Where(v => v.LoopTemplateId == templateId).ToListAsync();

    public async Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId)
        => await _db.LoopNodes.Where(n => n.LoopTemplateVersionId == versionId).ToListAsync();

    public async Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForVersionAsync(Guid versionId)
    {
        return await _db.LoopNodeEdges
            .Where(e => _db.LoopNodes.Any(n => n.Id == e.SourceNodeId && n.LoopTemplateVersionId == versionId))
            .ToListAsync();
    }

    public async Task<int> GetNextVersionNumberAsync(Guid templateId)
    {
        var max = await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync();
        return (max ?? 0) + 1;
    }

    public Task CreateTemplateAsync(LoopTemplate template)
    {
        _db.LoopTemplates.Add(template);
        return Task.CompletedTask;
    }

    public Task UpdateTemplateAsync(LoopTemplate template)
    {
        _db.LoopTemplates.Update(template);
        return Task.CompletedTask;
    }

    public async Task DeleteTemplateAsync(LoopTemplate template)
    {
        _db.LoopTemplates.Remove(template);
        await _db.SaveChangesAsync();
    }

    public Task CreateVersionAsync(LoopTemplateVersion version)
    {
        _db.LoopTemplateVersions.Add(version);
        return Task.CompletedTask;
    }

    public Task CreateNodesAsync(IReadOnlyList<LoopNode> nodes)
    {
        _db.LoopNodes.AddRange(nodes);
        return Task.CompletedTask;
    }

    public Task CreateEdgesAsync(IReadOnlyList<LoopNodeEdge> edges)
    {
        _db.LoopNodeEdges.AddRange(edges);
        return Task.CompletedTask;
    }

    public async Task DeleteNodesForVersionAsync(Guid versionId)
    {
        var nodes = await _db.LoopNodes.Where(n => n.LoopTemplateVersionId == versionId).ToListAsync();
        _db.LoopNodes.RemoveRange(nodes);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteEdgesForVersionAsync(Guid versionId)
    {
        var edges = await _db.LoopNodeEdges
            .Where(e => _db.LoopNodes.Any(n => n.Id == e.SourceNodeId && n.LoopTemplateVersionId == versionId))
            .ToListAsync();
        _db.LoopNodeEdges.RemoveRange(edges);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteVersionsForTemplateAsync(Guid templateId)
    {
        var versions = await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .ToListAsync();
        _db.LoopTemplateVersions.RemoveRange(versions);
        await _db.SaveChangesAsync();
    }

    public async Task SaveChangesAsync()
    {
        await _db.SaveChangesAsync();
    }
}
