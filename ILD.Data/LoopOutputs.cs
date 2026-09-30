using System.Text.Json.Nodes;
using ILD.Data.Enums;

namespace ILD.Data;

/// <summary>
/// The one definition of a node's outputs. A node declares every output once,
/// as an object in <c>config.outputs</c> (<c>{ "name": ..., ... }</c>); rules,
/// cases and edge rows only refer to an output by name. Each node type also has
/// <em>fixed</em> outputs it always holds — success and failure, and on a PR node
/// the reserved outputs the PR heartbeat fires — which are filled in when missing
/// and cannot be removed. See "Output" in CONTEXT.md and ADR-0023.
/// </summary>
public static class LoopOutputs
{
    public const string OnSuccess = "OnSuccess";
    public const string OnFailure = "OnFailure";

    public const string OnRejected = "on_rejected";
    public const string OnMergeConflict = "on_merge_conflict";
    public const string OnCiFailed = "on_ci_failed";
    public const string OnComment = "on_comment";
    public const string OnApproved = "on_approved";
    public const string OnCiPassed = "on_ci_passed";
    public const string OnMerged = "on_merged";
    public const string OnAbandoned = "on_abandoned";

    /// <summary>The reserved PR outputs in descending firing priority (index 0 = highest).</summary>
    public static readonly IReadOnlyList<string> ReservedPr = new[]
    {
        OnRejected,
        OnMergeConflict,
        OnCiFailed,
        OnComment,
        OnApproved,
        OnCiPassed,
        OnMerged,
        OnAbandoned,
    };

    private static readonly string[] SuccessAndFailure = { OnSuccess, OnFailure };
    private static readonly string[] FailureOnly = { OnFailure };
    private static readonly string[] PrFixed = SuccessAndFailure.Concat(ReservedPr).ToArray();

    /// <summary>
    /// The outputs a node of <paramref name="type"/> always holds. A Condition
    /// routes through its cases instead of a success output, and Cleanup is a sink.
    /// </summary>
    public static IReadOnlyList<string> Fixed(NodeType type) => type switch
    {
        NodeType.PR => PrFixed,
        NodeType.Condition => FailureOnly,
        NodeType.Cleanup => Array.Empty<string>(),
        _ => SuccessAndFailure,
    };

    /// <summary>Whether <paramref name="name"/> is one of the outputs that route through an edge's own type rather than a name.</summary>
    public static bool IsSuccessOrFailure(string name) => name is OnSuccess or OnFailure;

    public static bool IsReserved(NodeType type, string name)
        => type == NodeType.PR && ReservedPr.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Brings a node config to the saved shape: creates <c>outputs</c>, appends
    /// missing fixed outputs, and makes <c>reserved: true</c> appear on exactly the
    /// reserved outputs. Existing entries and keys are never reordered or dropped.
    /// A present but malformed <c>outputs</c> is left for the validator to report.
    /// Returns whether anything changed.
    /// </summary>
    public static bool NormalizeOutputs(NodeType? type, JsonObject config)
    {
        if (OutputsOf(config, out var changed) is not { } outputs) return false;

        if (type is { } known)
            foreach (var name in Fixed(known))
                changed |= AppendMissing(outputs, name);

        foreach (var output in outputs.OfType<JsonObject>())
        {
            var reserved = type is { } t && NameOf(output) is { } name && IsReserved(t, name);
            var flagged = output.TryGetPropertyValue("reserved", out var flag);
            if (reserved && !(flag is JsonValue v && v.TryGetValue<bool>(out var b) && b))
            {
                output["reserved"] = true;
                changed = true;
            }
            else if (!reserved && flagged)
            {
                output.Remove("reserved");
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Converts a config written before outputs existed: every name the old model
    /// used to define an output — <c>customEdges</c> (which is then dropped), AI
    /// <c>matchRules</c>, Condition <c>cases</c>/<c>defaultEdge</c> (trimmed, as the
    /// executor trims them) and each outgoing edge in <paramref name="outgoing"/> —
    /// is declared, then the config is normalized. A config that already has an
    /// <c>outputs</c> key is already converted and is left exactly as it is. Shared
    /// by the startup migrator and the document upgrader so the two cannot convert
    /// a node differently. An unknown <paramref name="type"/> (a document node whose
    /// type does not parse) gets the names only. Returns whether anything changed.
    /// </summary>
    public static bool UpgradeLegacyConfig(
        NodeType? type, JsonObject config, IEnumerable<(EdgeType Type, string? Name)> outgoing)
    {
        if (config.ContainsKey("outputs")) return false;

        var outputs = new JsonArray();
        config["outputs"] = outputs;

        if (type is { } known)
            foreach (var name in Fixed(known))
                AppendMissing(outputs, name);

        if (config["customEdges"] is JsonArray customEdges)
            foreach (var name in customEdges)
                AppendMissing(outputs, StringOf(name));
        config.Remove("customEdges");

        if (type is null or NodeType.AI && config["matchRules"] is JsonArray rules)
            foreach (var rule in rules)
                AppendMissing(outputs, StringOf((rule as JsonObject)?["edgeName"]));

        if (type is null or NodeType.Condition)
        {
            if (config["cases"] is JsonArray cases)
                foreach (var c in cases)
                    AppendMissing(outputs, StringOf((c as JsonObject)?["edgeName"])?.Trim());
            AppendMissing(outputs, StringOf(config["defaultEdge"])?.Trim());
        }

        foreach (var (edgeType, edgeName) in outgoing)
            AppendMissing(outputs, edgeType switch
            {
                EdgeType.OnSuccess => OnSuccess,
                EdgeType.OnFailure => OnFailure,
                _ => edgeName,
            });

        NormalizeOutputs(type, config);
        return true;
    }

    /// <summary>The <c>name</c> of an output object, or null when it has no string name.</summary>
    public static string? NameOf(JsonObject output) => StringOf(output["name"]);

    /// <summary>The config's <c>outputs</c> array, created when absent; null when present but not an array.</summary>
    private static JsonArray? OutputsOf(JsonObject config, out bool created)
    {
        created = false;
        var existing = config["outputs"];
        if (existing is JsonArray array) return array;
        if (existing is not null) return null;

        var outputs = new JsonArray();
        config["outputs"] = outputs;
        created = true;
        return outputs;
    }

    private static bool AppendMissing(JsonArray outputs, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (outputs.OfType<JsonObject>().Any(o => string.Equals(NameOf(o), name, StringComparison.Ordinal)))
            return false;
        outputs.Add(new JsonObject { ["name"] = name });
        return true;
    }

    private static string? StringOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
