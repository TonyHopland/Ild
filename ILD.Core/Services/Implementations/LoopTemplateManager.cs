using ILD.Core.DTOs;
using ILD.Core.Enums;
using ILD.Core.Models;
using ILD.Core.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public class LoopTemplateManager : ILoopTemplateManager
{
    private readonly AppDbContext _db;

    public LoopTemplateManager(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> CreateLoopTemplateAsync(string name, string description, LoopTemplateGraph graph)
    {
        var errors = LoopTemplateValidator.Validate(graph);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid loop template graph: " + string.Join("; ", errors));

        var template = new LoopTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            RecoveryPolicy = nameof(RecoveryPolicy.AutoResume),
            MaxNodeExecutions = 200,
            MaxWallClockHours = 24,
        };
        _db.LoopTemplates.Add(template);

        AddVersionFromGraph(template.Id, 1, graph);

        await _db.SaveChangesAsync();
        return template.Id;
    }

    public async Task<LoopTemplate?> GetLoopTemplateAsync(Guid templateId)
        => await _db.LoopTemplates.Include(t => t.Versions).FirstOrDefaultAsync(t => t.Id == templateId);

    public async Task<LoopTemplate?> GetLatestVersionAsync(Guid templateId)
        => await _db.LoopTemplates.Include(t => t.Versions).FirstOrDefaultAsync(t => t.Id == templateId);

    public async Task<IEnumerable<LoopTemplate>> GetAllLoopTemplatesAsync()
        => await _db.LoopTemplates.Include(t => t.Versions).ToListAsync();

    public async Task<Guid> UpdateLoopTemplateAsync(Guid templateId, string name, string description, LoopTemplateGraph graph)
    {
        var errors = LoopTemplateValidator.Validate(graph);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid loop template graph: " + string.Join("; ", errors));

        var template = await _db.LoopTemplates.FirstOrDefaultAsync(t => t.Id == templateId)
            ?? throw new InvalidOperationException($"Template {templateId} not found");

        template.Name = name;
        template.Description = description;
        template.UpdatedAt = DateTime.UtcNow;

        var nextVersion = (await _db.LoopTemplateVersions.Where(v => v.LoopTemplateId == templateId).MaxAsync(v => (int?)v.VersionNumber) ?? 0) + 1;
        AddVersionFromGraph(templateId, nextVersion, graph);

        await _db.SaveChangesAsync();
        return templateId;
    }

    public async Task<Guid> CloneLoopTemplateAsync(Guid sourceTemplateId, string newName)
    {
        var src = await _db.LoopTemplates
            .Include(t => t.Versions)
            .FirstOrDefaultAsync(t => t.Id == sourceTemplateId)
            ?? throw new InvalidOperationException($"Template {sourceTemplateId} not found");

        var latest = src.Versions.OrderByDescending(v => v.VersionNumber).First();

        var graph = await BuildGraphFromVersion(latest);
        return await CreateLoopTemplateAsync(newName, src.Description ?? string.Empty, graph);
    }

    public async Task<LoopTemplateVersion> GetVersionAsync(Guid templateId, int version)
        => await _db.LoopTemplateVersions
            .Include(v => v.Nodes)
            .FirstOrDefaultAsync(v => v.LoopTemplateId == templateId && v.VersionNumber == version)
            ?? throw new InvalidOperationException($"Version {version} not found for template {templateId}");

    public async Task<IEnumerable<LoopTemplateVersion>> GetVersionsAsync(Guid templateId)
        => await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();

    public Task<bool> ValidateGraphAsync(LoopTemplateGraph graph)
        => Task.FromResult(LoopTemplateValidator.Validate(graph).Count == 0);

    public async Task DeleteLoopTemplateAsync(Guid templateId)
    {
        var template = await _db.LoopTemplates.FindAsync(templateId);
        if (template == null) return;
        _db.LoopTemplates.Remove(template);
        await _db.SaveChangesAsync();
    }

    private void AddVersionFromGraph(Guid templateId, int versionNumber, LoopTemplateGraph graph)
    {
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = templateId,
            VersionNumber = versionNumber,
        };
        _db.LoopTemplateVersions.Add(version);

        var idMap = new Dictionary<string, Guid>();

        foreach (var n in graph.Nodes)
        {
            var nodeId = Guid.NewGuid();
            idMap[n.Id] = nodeId;

            if (!Enum.TryParse<NodeType>(n.NodeType, ignoreCase: true, out var type))
                type = NodeType.Cmd;

            _db.LoopNodes.Add(new LoopNode
            {
                Id = nodeId,
                LoopTemplateVersionId = version.Id,
                NodeType = type,
                Label = string.IsNullOrEmpty(n.Label) ? n.Id : n.Label,
                Config = System.Text.Json.JsonSerializer.Serialize(n.Config),
                MaxRetries = n.RetryCount ?? 0,
                TimeoutSeconds = n.TimeoutSeconds ?? 300,
            });
        }

        foreach (var e in graph.Edges)
        {
            if (!idMap.TryGetValue(e.SourceNodeId, out var srcId)) continue;
            if (!idMap.TryGetValue(e.TargetNodeId, out var tgtId)) continue;

            var edgeType = string.Equals(e.EdgeType, "OnFailure", StringComparison.OrdinalIgnoreCase) ? EdgeType.OnFailure : EdgeType.OnSuccess;

            var srcDto = graph.Nodes.First(n => n.Id == e.SourceNodeId);

            _db.LoopNodeEdges.Add(new LoopNodeEdge
            {
                Id = Guid.NewGuid(),
                SourceNodeId = srcId,
                TargetNodeId = tgtId,
                EdgeType = edgeType,
                MaxTraversals = srcDto.MaxTraversals,
            });
        }
    }

    private async Task<LoopTemplateGraph> BuildGraphFromVersion(LoopTemplateVersion v)
    {
        var nodes = await _db.LoopNodes.Where(n => n.LoopTemplateVersionId == v.Id).ToListAsync();
        var nodeIdSet = nodes.Select(n => n.Id).ToList();
        var edges = await _db.LoopNodeEdges.Where(e => nodeIdSet.Contains(e.SourceNodeId)).ToListAsync();

        // Map back DB node guid -> short id (use guid string for stable round-trip)
        var nodeDtos = nodes.Select(n => new LoopNodeDto
        {
            Id = n.Id.ToString(),
            NodeType = n.NodeType.ToString(),
            Label = n.Label,
            Config = string.IsNullOrEmpty(n.Config)
                ? new()
                : (System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(n.Config) ?? new()),
            RetryCount = n.MaxRetries,
            TimeoutSeconds = n.TimeoutSeconds,
        }).ToList();

        var edgeDtos = edges.Select(e => new LoopNodeEdgeDto
        {
            Id = e.Id.ToString(),
            SourceNodeId = e.SourceNodeId.ToString(),
            TargetNodeId = e.TargetNodeId.ToString(),
            EdgeType = e.EdgeType.ToString(),
        }).ToList();

        return new LoopTemplateGraph(v.Id, nodeDtos, edgeDtos);
    }
}
