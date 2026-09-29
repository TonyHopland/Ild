using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data.Enums;

namespace ILD.Data;

/// <summary>
/// Brings a loop document written in the old format (<c>ild-loop-template/v1</c>,
/// or no <c>$schema</c> at all) up to <c>ild-loop-template/v2</c>, where every
/// node declares its outputs in <c>config.outputs</c>. Each node goes through
/// <see cref="LoopOutputs.UpgradeLegacyConfig"/> with its edges from the same
/// document — the conversion the startup migrator applies to stored loops.
/// Everything else in the document is kept as it was, and a document that is
/// already v2 (or any other format) is never touched.
/// </summary>
public static class LoopDocumentUpgrader
{
    public const string V1 = "ild-loop-template/v1";
    public const string V2 = "ild-loop-template/v2";

    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    /// <summary>
    /// Returns <paramref name="document"/> upgraded to v2, or exactly the input when
    /// it is not a JSON object or not a v1 document.
    /// </summary>
    public static string Upgrade(string document)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(document);
        }
        catch (JsonException)
        {
            return document;
        }

        return parsed is JsonObject root && TryUpgrade(root) ? root.ToJsonString(OutputOptions) : document;
    }

    /// <summary>Upgrades <paramref name="root"/> in place; returns false, changing nothing, unless it is a v1 document.</summary>
    public static bool TryUpgrade(JsonObject root)
    {
        var hasSchema = root.TryGetPropertyValue("$schema", out var schema);
        if (hasSchema && Text(schema) != V1)
            return false;

        var outgoing = (root["edges"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Select(e => (Source: Text(e["sourceNodeId"]), Type: ParseName<EdgeType>(Text(e["edgeType"])), Name: Text(e["name"])))
            .Where(e => e.Source != null && e.Type != null)
            .ToLookup(e => e.Source!, e => (e.Type!.Value, e.Name), StringComparer.Ordinal);

        foreach (var node in (root["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (node["config"] is null)
                node["config"] = new JsonObject();
            if (node["config"] is JsonObject config)
                LoopOutputs.UpgradeLegacyConfig(
                    ParseName<NodeType>(Text(node["type"])), config, outgoing[Text(node["id"]) ?? string.Empty]);
        }

        if (hasSchema)
            root["$schema"] = V2;
        else
            root.Insert(0, "$schema", V2);
        return true;
    }

    private static TEnum? ParseName<TEnum>(string? text) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value : null;

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
