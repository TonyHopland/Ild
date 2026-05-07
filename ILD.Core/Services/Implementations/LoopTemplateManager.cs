using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class LoopTemplateManager : ILoopTemplateManager
{
    private readonly ILoopTemplateStore _store;

    public LoopTemplateManager(ILoopTemplateStore store)
    {
        _store = store;
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
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            MaxNodeExecutions = 200,
            MaxWallClockHours = 24,
        };
        await _store.CreateTemplateAsync(template);

        await AddVersionFromGraph(template.Id, 1, graph);
        await _store.SaveChangesAsync();

        return template.Id;
    }

    public async Task<LoopTemplate?> GetLoopTemplateAsync(Guid templateId)
        => await _store.GetByIdAsync(templateId);

    public async Task<LoopTemplate?> GetLatestVersionAsync(Guid templateId)
        => await _store.GetByIdAsync(templateId);

    public async Task<IEnumerable<LoopTemplate>> GetAllLoopTemplatesAsync(int skip = 0, int take = 100)
        => await _store.GetAllAsync(skip, take);

    public async Task<Guid> UpdateLoopTemplateAsync(Guid templateId, string name, string description, LoopTemplateGraph graph)
    {
        var errors = LoopTemplateValidator.Validate(graph);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid loop template graph: " + string.Join("; ", errors));

        var template = await _store.GetByIdAsync(templateId)
            ?? throw new InvalidOperationException($"Template {templateId} not found");

        template.Name = name;
        template.Description = description;
        template.UpdatedAt = DateTime.UtcNow;
        await _store.UpdateTemplateAsync(template);

        var nextVersion = await _store.GetNextVersionNumberAsync(templateId);
        await AddVersionFromGraph(templateId, nextVersion, graph);
        await _store.SaveChangesAsync();

        return templateId;
    }

    public async Task<Guid> CloneLoopTemplateAsync(Guid sourceTemplateId, string newName)
    {
        var src = await _store.GetByIdAsync(sourceTemplateId)
            ?? throw new InvalidOperationException($"Template {sourceTemplateId} not found");

        var latest = await _store.GetLatestVersionAsync(sourceTemplateId);
        if (latest == null)
            throw new InvalidOperationException($"No versions found for template {sourceTemplateId}");

        var graph = await BuildGraphFromVersion(latest);
        return await CreateLoopTemplateAsync(newName, src.Description ?? string.Empty, graph);
    }

    public async Task<LoopTemplateVersion> GetVersionAsync(Guid templateId, int version)
        => await _store.GetVersionAsync(templateId, version)
            ?? throw new InvalidOperationException($"Version {version} not found for template {templateId}");

    public async Task<IEnumerable<LoopTemplateVersion>> GetVersionsAsync(Guid templateId)
        => await _store.GetVersionsAsync(templateId);

    public async Task<LoopTemplateGraph?> GetLatestGraphAsync(Guid templateId)
    {
        var latest = await _store.GetLatestVersionAsync(templateId);
        if (latest == null) return null;
        return await BuildGraphFromVersion(latest);
    }

    public async Task<LoopTemplateGraph?> GetVersionGraphAsync(Guid templateId, int versionNumber)
    {
        var version = await _store.GetVersionAsync(templateId, versionNumber);
        if (version == null) return null;
        return await BuildGraphFromVersion(version);
    }

    public Task<(bool Valid, IReadOnlyList<string> Errors)> ValidateGraphAsync(LoopTemplateGraph graph)
    {
        var errors = LoopTemplateValidator.Validate(graph);
        return Task.FromResult((errors.Count == 0, errors));
    }

    public async Task DeleteLoopTemplateAsync(Guid templateId)
    {
        var template = await _store.GetByIdAsync(templateId);
        if (template == null) return;
        await _store.DeleteTemplateAsync(template);
    }

    private async Task AddVersionFromGraph(Guid templateId, int versionNumber, LoopTemplateGraph graph)
    {
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = templateId,
            VersionNumber = versionNumber,
        };
        await _store.CreateVersionAsync(version);

        var idMap = new Dictionary<string, Guid>();

        var nodes = graph.Nodes.Select(n =>
        {
            var nodeId = Guid.NewGuid();
            idMap[n.Id] = nodeId;

            if (!Enum.TryParse<NodeType>(n.NodeType, ignoreCase: true, out var type))
                type = NodeType.Cmd;

            return new LoopNode
            {
                Id = nodeId,
                LoopTemplateVersionId = version.Id,
                NodeType = type,
                Label = string.IsNullOrEmpty(n.Label) ? n.Id : n.Label,
                Config = System.Text.Json.JsonSerializer.Serialize(n.Config),
                MaxRetries = n.RetryCount ?? 0,
                TimeoutSeconds = n.TimeoutSeconds ?? 300,
            };
        }).ToList();

        await _store.CreateNodesAsync(nodes);

        var edges = graph.Edges.Select(e =>
        {
            if (!idMap.TryGetValue(e.SourceNodeId, out var srcId)) return null;
            if (!idMap.TryGetValue(e.TargetNodeId, out var tgtId)) return null;

            var edgeType = Enum.TryParse<EdgeType>(e.EdgeType, ignoreCase: true, out var parsed) ? parsed : EdgeType.OnSuccess;
            var srcDto = graph.Nodes.First(n => n.Id == e.SourceNodeId);

            return new LoopNodeEdge
            {
                Id = Guid.NewGuid(),
                SourceNodeId = srcId,
                TargetNodeId = tgtId,
                EdgeType = edgeType,
                MaxTraversals = srcDto.MaxTraversals,
            };
        }).Where(e => e != null).Cast<LoopNodeEdge>().ToList();

        if (edges.Count > 0)
            await _store.CreateEdgesAsync(edges);
    }

    private async Task<LoopTemplateGraph> BuildGraphFromVersion(LoopTemplateVersion v)
    {
        var nodes = await _store.GetNodesForVersionAsync(v.Id);
        var edges = await _store.GetEdgesForVersionAsync(v.Id);

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
