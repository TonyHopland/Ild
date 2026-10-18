using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>The API's response serialization: camelCase, the default encoder, compact, enums as strings.</summary>
    private static readonly JsonSerializerOptions RenderOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static LoopTemplateDocument From(LoopTemplate template, LoopTemplateGraph graph)
        => new(
            LoopDocumentUpgrader.V2,
            template.Name,
            template.Description ?? string.Empty,
            template.RecoveryPolicy.ToString(),
            graph.Nodes,
            graph.Edges);

    /// <summary>
    /// The document as text, exactly as <c>get_loop_template</c> returns it. The
    /// default encoder escapes <c>'</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>,
    /// <c>+</c> and non-ASCII as <c>\uXXXX</c>; an <c>editFile</c> matches this text.
    /// </summary>
    public string Render() => JsonSerializer.Serialize(this, RenderOptions);

    /// <summary>A parsed document re-rendered in <see cref="Render()"/>'s layout, so an edit to it keeps matching.</summary>
    public static string Render(JsonNode document) => document.ToJsonString(RenderOptions);
}
