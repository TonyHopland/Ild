using ILD.Core.Services.Implementations.Executors;
using ILD.Data.DTOs;
using ILD.Data.Enums;
using System.Linq;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

public static class LoopTemplateValidator
{
    // Every node (except the Cleanup sink) routes success and failure. Only
    // Human, AI, PR and Condition nodes may additionally declare named custom
    // edges (a Condition switch declares one per case plus its default edge).
    private static readonly HashSet<string> CustomEdgeNodeTypes =
        new(new[] { "Human", "AI", "PR", "Condition" }, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NoEdgeNodeTypes =
        new(new[] { "Cleanup" }, StringComparer.OrdinalIgnoreCase);

    private static bool AllowsCustomEdges(string nodeType) => CustomEdgeNodeTypes.Contains(nodeType);

    /// <summary>
    /// Text a match-rule pattern is trial-run against to see whether it can
    /// match nothing at all. Covers letters, digits, spaces and punctuation so a
    /// zero-width pattern has somewhere to land; the empty string catches the
    /// patterns that match even that.
    /// </summary>
    private static readonly string[] MatchRuleProbes = { string.Empty, "approve reject nits 12." };

    /// <summary>
    /// Ceiling on a single probe run. The probes are short enough that no
    /// realistic pattern approaches this, but a validator is reachable from any
    /// save request, so it must not be possible to wedge it with a
    /// catastrophically-backtracking pattern. Matches the executor's own
    /// per-rule timeout.
    /// </summary>
    private static readonly TimeSpan MatchRuleProbeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Checks an AI match rule's pattern at save time — the only point where the
    /// author can still fix it. Two ways a pattern is unusable:
    ///
    /// <list type="bullet">
    ///   <item>it does not compile, so the executor can never evaluate it and
    ///         the rule silently never fires;</item>
    ///   <item>it can match zero characters (<c>x*</c>, <c>\b</c>, <c>(?:)</c>,
    ///         <c>(?=...)</c>). Under last-match-wins such a pattern matches at
    ///         the very end of any output, so it outranks every genuine verdict
    ///         and the node always takes that one edge.</item>
    /// </list>
    ///
    /// Both misroute silently at run time, which is why they are worth blocking
    /// here rather than leaving to be discovered mid-run. Patterns that merely
    /// contain an optional part (<c>nits*</c>) still require real characters and
    /// are left alone.
    /// </summary>
    private static void ValidateMatchRulePattern(
        string nodeId, NodeConfig.AiMatchRule rule, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern)) return;

        Regex compiled;
        try
        {
            compiled = new Regex(rule.Pattern, RegexOptions.IgnoreCase, MatchRuleProbeTimeout);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"AI node {nodeId} has a match rule whose pattern '{rule.Pattern}' is not a valid regex: {ex.Message}");
            return;
        }

        bool matchesNothing;
        try
        {
            matchesNothing = MatchRuleProbes.Any(probe => compiled.Matches(probe).Any(m => m.Length == 0));
        }
        catch (RegexMatchTimeoutException)
        {
            // Too slow on a 23-character probe means hopeless against a real AI
            // output, where the executor would time out and skip it silently.
            // Reject it here, where the author still gets told why.
            errors.Add($"AI node {nodeId} has a match rule whose pattern '{rule.Pattern}' is too slow to evaluate (it backtracks excessively). Simplify it — nested quantifiers such as (a+)+ are the usual cause.");
            return;
        }

        if (matchesNothing)
            errors.Add($"AI node {nodeId} has a match rule whose pattern '{rule.Pattern}' can match an empty string, so it would match every AI output and always win. Make it match the verdict text itself.");
    }

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
            List<string>? conditionTemplates = null;
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

                foreach (var rule in cfg.MatchRules ?? new())
                    ValidateMatchRulePattern(node.Id, rule, errors);
            }
            else if (string.Equals(node.NodeType, "Condition", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = NodeConfig.Parse<NodeConfig.Condition>(System.Text.Json.JsonSerializer.Serialize(node.Config));
                var cases = cfg.Cases ?? new List<NodeConfig.ConditionCase>();
                var defaultEdge = (cfg.DefaultEdge ?? string.Empty).Trim();

                conditionTemplates = new List<string> { cfg.Output ?? ConditionNodeExecutor.DefaultTemplate };

                // A Condition routes only through named custom edges — never an
                // OnSuccess default. Collect the custom names actually wired out.
                var wiredCustomNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in edges.Where(e => e.SourceNodeId == node.Id))
                {
                    if (!Enum.TryParse<EdgeType>(e.EdgeType, ignoreCase: true, out var role)) continue;
                    if (role == EdgeType.OnSuccess)
                        errors.Add($"Condition node {node.Id} must not have an OnSuccess edge; it routes via its cases and default edge.");
                    else if (role == EdgeType.Custom && !string.IsNullOrWhiteSpace(e.Name))
                        wiredCustomNames.Add(e.Name!.Trim());
                }

                // A switch must name its default edge and have at least one case.
                if (defaultEdge.Length == 0)
                    errors.Add($"Condition node {node.Id} must set a default edge.");
                if (cases.Count == 0)
                    errors.Add($"Condition node {node.Id} must have at least one case.");

                // Referenced edge names = every case's edge plus the default.
                // Every referenced name must be wired and every wired custom edge
                // must be referenced (mirrors the AI node's match-rule/edge sync).
                // Names are ordinal to match the engine's edge resolution.
                var referenced = new HashSet<string>(StringComparer.Ordinal);
                if (defaultEdge.Length > 0)
                    referenced.Add(defaultEdge);
                for (var i = 0; i < cases.Count; i++)
                {
                    var c = cases[i];
                    var edgeName = (c.EdgeName ?? string.Empty).Trim();
                    if (edgeName.Length == 0)
                        errors.Add($"Condition node {node.Id} case {i + 1} must set an edge name.");
                    else
                        referenced.Add(edgeName);

                    var variant = (c.Variant ?? string.Empty).Trim();
                    if (string.Equals(variant, "TextMatches", StringComparison.OrdinalIgnoreCase))
                    {
                        conditionTemplates.Add(c.Subject ?? ConditionNodeExecutor.DefaultTemplate);
                        if (string.IsNullOrWhiteSpace(c.Pattern))
                            errors.Add($"Condition node {node.Id} case {i + 1} (TextMatches) must set a non-empty pattern.");
                        else
                        {
                            try { _ = new Regex(c.Pattern); }
                            catch (ArgumentException ex) { errors.Add($"Condition node {node.Id} case {i + 1} has an invalid regex pattern: {ex.Message}"); }
                        }
                    }
                    else if (string.Equals(variant, "HasTag", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(c.Tag))
                            errors.Add($"Condition node {node.Id} case {i + 1} (HasTag) must set a non-empty tag.");
                    }
                    else if (!string.Equals(variant, "PrExists", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Condition node {node.Id} case {i + 1} has an unknown variant '{c.Variant}'.");
                    }
                }

                foreach (var orphan in wiredCustomNames.Except(referenced))
                    errors.Add($"Condition node {node.Id} has a custom edge '{orphan}' that no case or default routes to.");
                foreach (var missing in referenced.Except(wiredCustomNames))
                    errors.Add($"Condition node {node.Id} routes to '{missing}' but no custom edge with that name exists.");
            }

            var templates = new[] { aiPrompt, prTemplate, prCommentTemplate, humanPrompt, promptNodePrompt }
                .Where(t => !string.IsNullOrEmpty(t))
                .Concat(conditionTemplates ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            if (templates.Count == 0) continue;
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
