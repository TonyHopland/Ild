using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;

namespace ILD.Data;

/// <summary>
/// How two loops differ as a person reads them: nodes matched by id, edges by
/// (source, target, type, name). Layout (<c>config.__pos</c>, which the Loop
/// Editor writes) and edge ids carry no meaning and are ignored.
/// </summary>
public static class LoopTemplateChanges
{
    private const string LayoutKey = "__pos";

    /// <summary>What <paramref name="proposedDocument"/> changes in <paramref name="baseGraph"/>.</summary>
    public static LoopTemplateChangeSummary Diff(TemplateGraph baseGraph, string proposedDocument)
    {
        var before = Loop.From(baseGraph);
        var after = Loop.Parse(proposedDocument);

        var beforeNodes = ById(before.Nodes);
        var afterNodes = ById(after.Nodes);
        var changed = new List<ChangedLoopNode>();
        foreach (var node in after.Nodes)
        {
            if (!beforeNodes.TryGetValue(node.Id, out var old)) continue;
            var fields = FieldsThatDiffer(old, node);
            if (fields.Count > 0) changed.Add(new ChangedLoopNode(node.Id, node.Label, node.Type, fields));
        }

        return new LoopTemplateChangeSummary(
            NameChanged: before.Name != after.Name,
            DescriptionChanged: before.Description != after.Description,
            RecoveryPolicyChanged: before.RecoveryPolicy != after.RecoveryPolicy,
            NodesAdded: after.Nodes.Where(n => !beforeNodes.ContainsKey(n.Id)).Select(Ref).ToList(),
            NodesRemoved: before.Nodes.Where(n => !afterNodes.ContainsKey(n.Id)).Select(Ref).ToList(),
            NodesChanged: changed,
            EdgesAdded: EdgesOnlyIn(after, before),
            EdgesRemoved: EdgesOnlyIn(before, after));
    }

    /// <summary>
    /// Whether two documents hold the same loop: name, description, recovery
    /// policy, nodes (id, type, label, config) and edges, in any order.
    /// </summary>
    public static bool Equivalent(string document, string other)
    {
        var a = Loop.Parse(document);
        var b = Loop.Parse(other);
        if (a.Name != b.Name || a.Description != b.Description || a.RecoveryPolicy != b.RecoveryPolicy)
            return false;
        if (a.Nodes.Count != b.Nodes.Count) return false;
        var nodes = ById(b.Nodes);
        if (a.Nodes.Any(n => !nodes.TryGetValue(n.Id, out var match) || FieldsThatDiffer(n, match).Count > 0))
            return false;
        return EdgeKeys(a).Order(StringComparer.Ordinal).SequenceEqual(EdgeKeys(b).Order(StringComparer.Ordinal));
    }

    /// <summary>A save does not refuse two nodes with one id; only the first is compared.</summary>
    private static Dictionary<string, Node> ById(IEnumerable<Node> nodes)
        => nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static LoopNodeRef Ref(Node node) => new(node.Id, node.Label, node.Type);

    private static List<string> FieldsThatDiffer(Node before, Node after)
    {
        var fields = new List<string>();
        if (before.Type != after.Type) fields.Add("type");
        if (before.Label != after.Label) fields.Add("label");
        foreach (var key in before.Config.Select(p => p.Key).Union(after.Config.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            if (!JsonNode.DeepEquals(before.Config[key], after.Config[key]))
                fields.Add($"config.{key}");
        }
        return fields;
    }

    private static IEnumerable<string> EdgeKeys(Loop loop)
        => loop.Edges.Select(e => $"{e.Source}\n{e.Target}\n{e.Type}\n{e.Name}");

    /// <summary>The edges of <paramref name="loop"/> that <paramref name="other"/> lacks, counted as a multiset.</summary>
    private static List<LoopEdgeRef> EdgesOnlyIn(Loop loop, Loop other)
    {
        var remaining = other.Edges.GroupBy(e => e).ToDictionary(g => g.Key, g => g.Count());
        var labels = ById(loop.Nodes);
        var only = new List<LoopEdgeRef>();
        foreach (var edge in loop.Edges)
        {
            if (remaining.TryGetValue(edge, out var count) && count > 0)
            {
                remaining[edge] = count - 1;
                continue;
            }
            only.Add(new LoopEdgeRef(
                labels.TryGetValue(edge.Source, out var source) ? source.Label : edge.Source,
                labels.TryGetValue(edge.Target, out var target) ? target.Label : edge.Target,
                edge.Type,
                edge.Name));
        }
        return only;
    }

    private sealed record Node(string Id, string Type, string Label, JsonObject Config);

    private sealed record Edge(string Source, string Target, string Type, string? Name);

    private sealed record Loop(string Name, string Description, string RecoveryPolicy, List<Node> Nodes, List<Edge> Edges)
    {
        public static Loop From(TemplateGraph graph)
            => new(
                graph.Template.Name,
                graph.Template.Description ?? string.Empty,
                graph.Template.RecoveryPolicy.ToString(),
                graph.Nodes.Select(n => new Node(
                    n.Id.ToString(),
                    n.NodeType.ToString(),
                    n.Label,
                    ConfigOf(string.IsNullOrEmpty(n.Config) ? null : JsonNode.Parse(n.Config)))).ToList(),
                graph.Edges.Select(e => new Edge(
                    e.SourceNodeId.ToString(),
                    e.TargetNodeId.ToString(),
                    e.EdgeType.ToString(),
                    NameOf(e.Name))).ToList());

        public static Loop Parse(string document)
        {
            var root = JsonNode.Parse(document) as JsonObject
                ?? throw new JsonException("A loop document is a JSON object.");
            return new(
                Text(root["name"]) ?? string.Empty,
                Text(root["description"]) ?? string.Empty,
                Canonical<RecoveryPolicy>(Text(root["recoveryPolicy"])),
                (root["nodes"] as JsonArray ?? []).OfType<JsonObject>().Select(n => new Node(
                    Text(n["id"]) ?? string.Empty,
                    Canonical<NodeType>(Text(n["type"])),
                    Text(n["label"]) ?? string.Empty,
                    ConfigOf(n["config"]))).ToList(),
                (root["edges"] as JsonArray ?? []).OfType<JsonObject>().Select(e => new Edge(
                    Text(e["sourceNodeId"]) ?? string.Empty,
                    Text(e["targetNodeId"]) ?? string.Empty,
                    Canonical<EdgeType>(Text(e["edgeType"])),
                    NameOf(Text(e["name"])))).ToList());
        }
    }

    private static JsonObject ConfigOf(JsonNode? config)
    {
        var copy = config?.DeepClone() as JsonObject ?? new JsonObject();
        copy.Remove(LayoutKey);
        return copy;
    }

    /// <summary>A save stores a blank edge name as none.</summary>
    private static string? NameOf(string? name) => string.IsNullOrWhiteSpace(name) ? null : name;

    /// <summary>An enum value as the save stores it, whatever its case; anything else as written.</summary>
    private static string Canonical<TEnum>(string? text) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value.ToString() : text ?? string.Empty;

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>A proposal's structural changes against its base version, as a person reviews them.</summary>
public sealed record LoopTemplateChangeSummary(
    bool NameChanged,
    bool DescriptionChanged,
    bool RecoveryPolicyChanged,
    IReadOnlyList<LoopNodeRef> NodesAdded,
    IReadOnlyList<LoopNodeRef> NodesRemoved,
    IReadOnlyList<ChangedLoopNode> NodesChanged,
    IReadOnlyList<LoopEdgeRef> EdgesAdded,
    IReadOnlyList<LoopEdgeRef> EdgesRemoved);

public sealed record LoopNodeRef(string Id, string Label, string Type);

/// <param name="Fields">What differs: <c>type</c>, <c>label</c> or <c>config.&lt;key&gt;</c>.</param>
public sealed record ChangedLoopNode(string Id, string Label, string Type, IReadOnlyList<string> Fields);

public sealed record LoopEdgeRef(string SourceLabel, string TargetLabel, string EdgeType, string? Name);
