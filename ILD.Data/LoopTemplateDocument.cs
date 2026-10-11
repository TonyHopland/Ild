using System.Text.Json.Serialization;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Data;

/// <summary>
/// A saved template version as an <c>ild-loop-template/v2</c> document, in the shape
/// the Loop Editor's export writes, so it can be handed to the same tools that read
/// and replace the open loop. Name, description and recovery policy live on the
/// template and are not versioned, so they are always the template's current values.
/// </summary>
public sealed record LoopTemplateDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    string Description,
    string RecoveryPolicy,
    IReadOnlyList<LoopNodeDto> Nodes,
    IReadOnlyList<LoopNodeEdgeDto> Edges)
{
    public static LoopTemplateDocument From(LoopTemplate template, LoopTemplateGraph graph)
        => new(
            LoopDocumentUpgrader.V2,
            template.Name,
            template.Description ?? string.Empty,
            template.RecoveryPolicy.ToString(),
            graph.Nodes,
            graph.Edges);
}
