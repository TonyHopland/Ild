using ILD.Data.DTOs;
using System.Linq;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

public static class LoopTemplateValidator
{
    private static readonly HashSet<string> KnownPlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "WorkItem.Title", "WorkItem.Description",
        "EventLog.LastN", "EventLog.Summary",
        "Node.Input", "PreviousNode.Output",
        "WorkTree.Diff",
    };

    private static readonly Regex PlaceholderPattern = new(@"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

    private static readonly Dictionary<string, HashSet<string>> AllowedEdgeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Human", new() { "OnSuccess", "OnFailure", "OnRespond" } },
        { "Start", new() { "OnSuccess", "OnFailure" } },
        { "Cmd", new() { "OnSuccess", "OnFailure" } },
        { "AI", new() { "OnSuccess", "OnFailure" } },
        { "PR", new() { "OnSuccess", "OnFailure" } },
        { "Cleanup", new() { "OnSuccess", "OnFailure" } },
    };

    private static HashSet<string> GetAllowedEdgeTypes(string nodeType)
    {
        if (AllowedEdgeTypes.TryGetValue(nodeType, out var types)) return types;
        return AllowedEdgeTypes["Cmd"];
    }

    public static IReadOnlyList<string> Validate(LoopTemplateGraph graph)
    {
        var errors = new List<string>();
        var nodes = graph.Nodes ?? new();
        var edges = graph.Edges ?? new();

        if (!nodes.Any(n => string.Equals(n.NodeType, "Start", StringComparison.OrdinalIgnoreCase)))
            errors.Add("Graph must contain a Start node.");

        if (!nodes.Any(n => string.Equals(n.NodeType, "Cleanup", StringComparison.OrdinalIgnoreCase)))
            errors.Add("Graph must contain a Cleanup node.");

        // Reachability: all nodes must be reachable from Start
        var startNode = nodes.FirstOrDefault(n => string.Equals(n.NodeType, "Start", StringComparison.OrdinalIgnoreCase));
        if (startNode != null)
        {
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(startNode.Id);
            reachable.Add(startNode.Id);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var e in edges.Where(e => e.SourceNodeId == cur))
                {
                    if (reachable.Add(e.TargetNodeId))
                        queue.Enqueue(e.TargetNodeId);
                }
            }

            var unreachable = nodes.Select(n => n.Id).Except(reachable).ToList();
            if (unreachable.Count > 0)
                errors.Add($"Unreachable nodes from Start: {string.Join(",", unreachable)}");

            // At least one path leads to a Cleanup node
            var cleanupNodeIds = nodes
                .Where(n => string.Equals(n.NodeType, "Cleanup", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Id)
                .ToHashSet();
            if (cleanupNodeIds.Count > 0 && !reachable.Any(id => cleanupNodeIds.Contains(id)))
                errors.Add("No path from Start leads to a Cleanup node.");
        }

        // Edge type per node: max one OnSuccess and one OnFailure per source
        foreach (var src in edges.GroupBy(e => e.SourceNodeId))
        {
            var dupes = src.GroupBy(e => e.EdgeType.ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var d in dupes)
                errors.Add($"Node {src.Key} has duplicate {d} edges.");
        }

        // Every edge must have a valid EdgeType for its source node type
        foreach (var e in edges)
        {
            var srcNode = nodes.FirstOrDefault(n => n.Id == e.SourceNodeId);
            var allowed = srcNode != null ? GetAllowedEdgeTypes(srcNode.NodeType) : null;
            if (string.IsNullOrEmpty(e.EdgeType) || allowed == null || !allowed.Contains(e.EdgeType, StringComparer.OrdinalIgnoreCase))
            {
                var allowedList = allowed != null ? string.Join(", ", allowed) : "none";
                errors.Add($"Edge {e.Id} has an invalid or missing EdgeType ('{e.EdgeType}'). Allowed for source node type: {allowedList}.");
            }
        }

        // Unknown placeholders in AI/Human prompt templates and PR description template
        foreach (var node in nodes)
        {
            var prompt = (node.Config.GetValueOrDefault("initialPrompt")
                ?? node.Config.GetValueOrDefault("loopPrompt"))?.ToString();
            var prTemplate = node.Config.GetValueOrDefault("prDescriptionTemplate")?.ToString();
            var humanPrompt = string.Equals(node.NodeType, "Human", StringComparison.OrdinalIgnoreCase)
                ? node.Config.GetValueOrDefault("prompt")?.ToString()
                : null;
            if (string.IsNullOrEmpty(prompt) && string.IsNullOrEmpty(prTemplate) && string.IsNullOrEmpty(humanPrompt)) continue;

            var templates = new[] { prompt, prTemplate, humanPrompt }.Where(t => !string.IsNullOrEmpty(t)).ToList();
            foreach (var template in templates)
            {
                foreach (Match m in PlaceholderPattern.Matches(template!))
                {
                    var key = m.Groups[1].Value;
                    if (key.StartsWith("WorkTree.File:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!KnownPlaceholders.Contains(key))
                        errors.Add($"Unknown placeholder '{{{{{key}}}}}' in node {node.Id}.");
                }
            }
        }

        return errors;
    }
}
