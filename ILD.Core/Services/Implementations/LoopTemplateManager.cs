using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data;
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

    public async Task<Guid> CreateLoopTemplateAsync(string name, string description, LoopTemplateGraph graph, RecoveryPolicy recoveryPolicy = RecoveryPolicy.AutoResume)
    {
        NormalizeOutputs(graph);
        var errors = LoopTemplateValidator.Validate(graph);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid loop template graph: " + string.Join("; ", errors));

        var template = new LoopTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            RecoveryPolicy = recoveryPolicy,
        };
        await _store.CreateTemplateAsync(template);

        var (version, nodes, edges) = BuildVersion(graph);
        version.LoopTemplateId = template.Id;
        version.VersionNumber = 1;
        await _store.CreateVersionAsync(version);
        await _store.CreateNodesAsync(nodes);
        if (edges.Count > 0)
            await _store.CreateEdgesAsync(edges);
        await _store.SaveChangesAsync();

        return template.Id;
    }

    public async Task<LoopTemplate?> GetLoopTemplateAsync(Guid templateId)
        => await _store.GetByIdAsync(templateId);

    public async Task<LoopTemplate?> GetLatestVersionAsync(Guid templateId)
        => await _store.GetByIdAsync(templateId);

    public async Task<IEnumerable<LoopTemplate>> GetAllLoopTemplatesAsync(int skip = 0, int take = 100, bool includeArchived = false)
        => await _store.GetAllAsync(skip, take, includeArchived);

    public async Task<LoopTemplateVersionSave> SaveNewVersionAsync(
        Guid templateId, string name, string description, LoopTemplateGraph graph, RecoveryPolicy recoveryPolicy, Guid? fromProposalId = null)
    {
        NormalizeOutputs(graph);
        var errors = LoopTemplateValidator.Validate(graph);
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid loop template graph: " + string.Join("; ", errors));
        return await _store.AddVersionAsync(WriteOf(templateId, name, description, recoveryPolicy, graph), fromProposalId, CancellationToken.None);
    }

    public async Task<LoopTemplateVersionSave> SaveProposalAsync(LoopTemplateProposal proposal)
    {
        var checkedDocument = CheckProposedDocument(proposal.Document);
        if (checkedDocument.Request is not { } request)
            throw new InvalidOperationException("The proposed loop no longer passes a save's checks: "
                + string.Join("; ", checkedDocument.ValidationErrors.Prepend(checkedDocument.Error).OfType<string>()));
        var write = WriteOf(
            proposal.LoopTemplateId,
            request.Name,
            request.Description,
            request.RecoveryPolicy,
            new LoopTemplateGraph(Guid.Empty, request.Nodes, request.Edges));
        return await _store.SaveProposalAsync(write, proposal.Id, CancellationToken.None);
    }

    public ProposedLoopDocument CheckProposedDocument(string document)
    {
        JsonObject? root;
        try
        {
            root = JsonText.Parse(document) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Refused($"The document is not valid JSON: {ex.Message}");
        }
        if (root is null)
            return Refused($"The document must be a JSON object: the whole {LoopDocumentUpgrader.V2} document get_loop_template returns.");
        if (!root.ContainsKey("$schema"))
            return Refused($"The document has no $schema. Send the whole {LoopDocumentUpgrader.V2} document get_loop_template returns, with your changes.");
        // Upgraded here rather than by the shared check: the schema and policy are
        // refused before the graph is read.
        LoopDocumentUpgrader.TryUpgrade(root);
        if (JsonText.Of(root["$schema"]) != LoopDocumentUpgrader.V2)
            return Refused($"$schema must be \"{LoopDocumentUpgrader.V2}\" (a {LoopDocumentUpgrader.V1} document is upgraded to it).");
        if (!Enum.TryParse<RecoveryPolicy>(JsonText.Of(root["recoveryPolicy"]), ignoreCase: true, out var policy) || !Enum.IsDefined(policy))
            return Refused($"recoveryPolicy must be one of {string.Join(", ", Enum.GetNames<RecoveryPolicy>())}.");

        var request = new LoopTemplateCreateRequest
        {
            Name = JsonText.Of(root["name"]) ?? string.Empty,
            Description = JsonText.Of(root["description"]) ?? string.Empty,
            RecoveryPolicy = policy,
        };
        // The request is refused for its name and description before its graph is
        // normalised or validated, which a malformed graph would not survive.
        var check = LoopDocumentEditor.Check(root, graph =>
        {
            var refused = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request, new ValidationContext(request), refused, validateAllProperties: true))
                return string.Join(" ", refused.Select(r => r.ErrorMessage));
            NormalizeOutputs(graph);
            return null;
        });
        if (check.Graph is not { } graph)
            return Refused($"The document's nodes or edges are not in the {LoopDocumentUpgrader.V2} shape: {check.ParseError}");
        if (check.Refusal is { } refusal)
            return Refused(refusal);
        request.Nodes = graph.Nodes;
        request.Edges = graph.Edges;

        return check.ValidationErrors.Count > 0
            ? new ProposedLoopDocument(null, null, null, check.ValidationErrors)
            : new ProposedLoopDocument(request, DocumentOf(request.Name, request.Description, policy, graph), null, []);
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

    public async Task<(int Version, string Document)?> GetLatestDocumentAsync(LoopTemplate template)
    {
        var latest = await _store.GetLatestVersionAsync(template.Id);
        if (latest == null) return null;
        var graph = await BuildGraphFromVersion(latest);
        return (latest.VersionNumber, LoopTemplateDocument.From(template, graph).Render());
    }

    public async Task<LoopTemplateGraph?> GetVersionGraphAsync(Guid templateId, int versionNumber)
    {
        var version = await _store.GetVersionAsync(templateId, versionNumber);
        if (version == null) return null;
        return await BuildGraphFromVersion(version);
    }

    public Task<(bool Valid, IReadOnlyList<string> Errors)> ValidateGraphAsync(LoopTemplateGraph graph)
    {
        NormalizeOutputs(graph);
        var errors = LoopTemplateValidator.Validate(graph);
        return Task.FromResult((errors.Count == 0, errors));
    }

    public async Task<StaledProposals> DeleteLoopTemplateAsync(Guid templateId)
    {
        var template = await _store.GetByIdAsync(templateId);
        if (template == null) return StaledProposals.None;
        return await _store.DeleteTemplateAsync(template);
    }

    public async Task ArchiveLoopTemplateAsync(Guid templateId)
    {
        await _store.SetArchivedAsync(templateId, true);
        await _store.SaveChangesAsync();
    }

    public async Task UnarchiveLoopTemplateAsync(Guid templateId)
    {
        await _store.SetArchivedAsync(templateId, false);
        await _store.SaveChangesAsync();
    }

    /// <summary>The type a node is stored as: one that does not parse is saved as Cmd.</summary>
    internal static NodeType StoredNodeType(string? nodeType)
        => Enum.TryParse<NodeType>(nodeType, ignoreCase: true, out var type) ? type : NodeType.Cmd;

    /// <summary>
    /// Brings every node config to its saved outputs shape (<see cref="LoopOutputs.NormalizeOutputs"/>)
    /// before validation, so fixed and reserved outputs are always stored.
    /// </summary>
    private static void NormalizeOutputs(LoopTemplateGraph graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (JsonSerializer.SerializeToNode(node.Config) is JsonObject config
                && LoopOutputs.NormalizeOutputs(StoredNodeType(node.NodeType), config))
                node.Config = config.Deserialize<Dictionary<string, object>>() ?? new();
        }
    }

    private static LoopTemplateVersionWrite WriteOf(
        Guid templateId, string name, string description, RecoveryPolicy recoveryPolicy, LoopTemplateGraph graph)
    {
        var (version, nodes, edges) = BuildVersion(graph);
        return new LoopTemplateVersionWrite(
            templateId, name, description, recoveryPolicy, version, nodes, edges,
            DocumentOf(name, description, recoveryPolicy, graph));
    }

    private static ProposedLoopDocument Refused(string error) => new(null, null, error, []);

    /// <summary>A loop as the <c>ild-loop-template/v2</c> document get_loop_template returns.</summary>
    private static string DocumentOf(string name, string description, RecoveryPolicy recoveryPolicy, LoopTemplateGraph graph)
        => new LoopTemplateDocument(LoopDocumentUpgrader.V2, name, description, recoveryPolicy.ToString(), graph.Nodes, graph.Edges).Render();

    /// <summary>A version of <paramref name="graph"/> ready to insert, but for its template and number.</summary>
    private static (LoopTemplateVersion Version, List<LoopNode> Nodes, List<LoopNodeEdge> Edges) BuildVersion(LoopTemplateGraph graph)
    {
        var version = new LoopTemplateVersion { Id = Guid.NewGuid() };

        var idMap = new Dictionary<string, Guid>();

        var nodes = graph.Nodes.Select(n =>
        {
            var nodeId = Guid.NewGuid();
            idMap[n.Id] = nodeId;

            return new LoopNode
            {
                Id = nodeId,
                LoopTemplateVersionId = version.Id,
                NodeType = StoredNodeType(n.NodeType),
                Label = string.IsNullOrEmpty(n.Label) ? n.Id : n.Label,
                Config = System.Text.Json.JsonSerializer.Serialize(n.Config),
            };
        }).ToList();

        var edges = graph.Edges.Select(e =>
        {
            if (!idMap.TryGetValue(e.SourceNodeId, out var srcId)) return null;
            if (!idMap.TryGetValue(e.TargetNodeId, out var tgtId)) return null;

            var edgeType = Enum.TryParse<EdgeType>(e.EdgeType, ignoreCase: true, out var parsed) ? parsed : EdgeType.OnSuccess;

            return new LoopNodeEdge
            {
                Id = Guid.NewGuid(),
                SourceNodeId = srcId,
                TargetNodeId = tgtId,
                EdgeType = edgeType,
                Name = string.IsNullOrWhiteSpace(e.Name) ? null : e.Name,
            };
        }).Where(e => e != null).Cast<LoopNodeEdge>().ToList();

        return (version, nodes, edges);
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
        }).ToList();

        var edgeDtos = edges.Select(e => new LoopNodeEdgeDto
        {
            Id = e.Id.ToString(),
            SourceNodeId = e.SourceNodeId.ToString(),
            TargetNodeId = e.TargetNodeId.ToString(),
            EdgeType = e.EdgeType.ToString(),
            Name = e.Name,
        }).ToList();

        return new LoopTemplateGraph(v.Id, nodeDtos, edgeDtos);
    }
}
