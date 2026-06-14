using ILD.Core.Services.Implementations.Executors;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using System.Linq;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

public static class LoopTemplateValidator
{
    // Every node (except the Cleanup sink) routes success and failure. Only
    // Human, AI and PR nodes may additionally declare named custom edges.
    private static readonly HashSet<string> CustomEdgeNodeTypes =
        new(new[] { "Human", "AI", "PR" }, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NoEdgeNodeTypes =
        new(new[] { "Cleanup" }, StringComparer.OrdinalIgnoreCase);

    private static bool AllowsCustomEdges(string nodeType) => CustomEdgeNodeTypes.Contains(nodeType);

    private static bool AllowsAnyEdges(string nodeType) => !NoEdgeNodeTypes.Contains(nodeType);

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

        // Per-source edge rules:
        //   • at most one OnSuccess (default) and one OnFailure (fallback)
        //   • custom edges allowed only on Human/AI/PR, each with a non-empty,
        //     node-unique Name
        //   • a sink node (Cleanup) takes no outgoing edges
        var nodeTypeById = nodes
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().NodeType, StringComparer.Ordinal);

        foreach (var src in edges.GroupBy(e => e.SourceNodeId))
        {
            var srcType = nodeTypeById.GetValueOrDefault(src.Key) ?? string.Empty;

            if (!AllowsAnyEdges(srcType))
            {
                errors.Add($"Node {src.Key} ({srcType}) must not have outgoing edges.");
                continue;
            }

            var seenCustomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var successCount = 0;
            var failureCount = 0;

            foreach (var e in src)
            {
                if (!Enum.TryParse<EdgeType>(e.EdgeType, ignoreCase: true, out var role))
                {
                    errors.Add($"Edge {e.Id} has an invalid or missing EdgeType ('{e.EdgeType}').");
                    continue;
                }

                switch (role)
                {
                    case EdgeType.OnSuccess:
                        if (++successCount > 1)
                            errors.Add($"Node {src.Key} has duplicate OnSuccess edges.");
                        break;
                    case EdgeType.OnFailure:
                        if (++failureCount > 1)
                            errors.Add($"Node {src.Key} has duplicate OnFailure edges.");
                        break;
                    case EdgeType.Custom:
                        if (!AllowsCustomEdges(srcType))
                        {
                            errors.Add($"Node {src.Key} ({srcType}) may not have custom edges; only Human, AI and PR nodes can.");
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(e.Name))
                        {
                            errors.Add($"Custom edge {e.Id} on node {src.Key} must have a Name.");
                            break;
                        }
                        if (!seenCustomNames.Add(e.Name))
                            errors.Add($"Node {src.Key} has duplicate custom edge '{e.Name}'.");
                        break;
                }
            }
        }

        // Unknown placeholders in AI/Human/Prompt prompt templates and PR description template
        foreach (var node in nodes)
        {
            var aiPrompt = string.Equals(node.NodeType, "AI", StringComparison.OrdinalIgnoreCase)
                ? node.Config.GetValueOrDefault("prompt")?.ToString()
                : null;
            var prTemplate = node.Config.GetValueOrDefault("prDescriptionTemplate")?.ToString();
            var prCommentTemplate = node.Config.GetValueOrDefault("prCommentTemplate")?.ToString();
            var humanPrompt = string.Equals(node.NodeType, "Human", StringComparison.OrdinalIgnoreCase)
                ? node.Config.GetValueOrDefault("prompt")?.ToString()
                : null;
            var promptNodePrompt = string.Equals(node.NodeType, "Prompt", StringComparison.OrdinalIgnoreCase)
                ? node.Config.GetValueOrDefault("prompt")?.ToString()
                : null;
            if (string.Equals(node.NodeType, "AI", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = NodeConfig.Parse<NodeConfig.Ai>(System.Text.Json.JsonSerializer.Serialize(node.Config));
                if (cfg.UseSession == true && string.IsNullOrWhiteSpace(cfg.SessionPlaceholder))
                    errors.Add($"AI node {node.Id} with useSession=true must set sessionPlaceholder.");

                // AI custom edges and match rules must stay in sync: every named
                // custom edge must be routed to by a match rule, and every rule
                // must point at an existing custom edge. Comparison is ordinal to
                // mirror the engine's edge resolution (LoopEngine.ResolveNextEdgeAsync).
                var ruleEdgeNames = (cfg.MatchRules ?? new())
                    .Where(r => !string.IsNullOrWhiteSpace(r.EdgeName))
                    .Select(r => r.EdgeName!)
                    .ToHashSet(StringComparer.Ordinal);
                var customEdgeNames = edges
                    .Where(e => e.SourceNodeId == node.Id && !string.IsNullOrWhiteSpace(e.Name)
                        && Enum.TryParse<EdgeType>(e.EdgeType, ignoreCase: true, out var role) && role == EdgeType.Custom)
                    .Select(e => e.Name!)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var orphan in customEdgeNames.Except(ruleEdgeNames))
                    errors.Add($"AI node {node.Id} has a custom edge '{orphan}' that no match rule routes to.");
                foreach (var missing in ruleEdgeNames.Except(customEdgeNames))
                    errors.Add($"AI node {node.Id} has a match rule routing to '{missing}' but no custom edge with that name exists.");
            }

            if (string.IsNullOrEmpty(aiPrompt) && string.IsNullOrEmpty(prTemplate) && string.IsNullOrEmpty(prCommentTemplate) && string.IsNullOrEmpty(humanPrompt) && string.IsNullOrEmpty(promptNodePrompt)) continue;

            var templates = new[] { aiPrompt, prTemplate, prCommentTemplate, humanPrompt, promptNodePrompt }.Where(t => !string.IsNullOrEmpty(t)).ToList();
            foreach (var template in templates)
            {
                foreach (Match m in PromptPlaceholderRegistry.Pattern.Matches(template!))
                {
                    var key = m.Groups[1].Value;
                    if (!PromptPlaceholderRegistry.IsKnown(key))
                        errors.Add($"Unknown placeholder '{{{{{key}}}}}' in node {node.Id}.");
                }
            }
        }

        return errors;
    }
}
