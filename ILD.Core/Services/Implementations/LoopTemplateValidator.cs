using ILD.Core.Services.Implementations.Executors;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

public static class LoopTemplateValidator
{
    // Every node (except the Cleanup sink) has its fixed outputs. Only these node
    // types may also declare named outputs of their own.
    private static readonly HashSet<NodeType> NamedOutputNodeTypes =
        new() { NodeType.Human, NodeType.AI, NodeType.PR, NodeType.Condition };

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

        // Every node's declared outputs: the valid names in config.outputs plus
        // the fixed outputs of its type, which it holds even when they are absent.
        var declaredById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var node in nodes)
            declaredById.TryAdd(node.Id, DeclaredOutputs(node, errors));
        // The named outputs each node has a Custom edge from. A rule, case or
        // default routing to an unwired output would fail the run when it routes,
        // so it is refused here.
        var wiredById = edges
            .Where(e => !string.IsNullOrWhiteSpace(e.Name)
                && Enum.TryParse<EdgeType>(e.EdgeType, ignoreCase: true, out var role) && role == EdgeType.Custom)
            .GroupBy(e => e.SourceNodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Name!).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        HashSet<string> WiredFrom(string nodeId) => wiredById.GetValueOrDefault(nodeId) ?? new(StringComparer.Ordinal);
        var nodeTypeById = nodes
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().NodeType, StringComparer.Ordinal);

        // Per-source edge rules:
        //   • at most one OnSuccess (default) and one OnFailure (fallback)
        //   • a Custom edge connects a named output the source declares, and the
        //     names are unique per node
        //   • a sink node (Cleanup) takes no outgoing edges
        foreach (var src in edges.GroupBy(e => e.SourceNodeId))
        {
            var srcTypeName = nodeTypeById.GetValueOrDefault(src.Key) ?? string.Empty;
            var srcType = LoopTemplateManager.StoredNodeType(srcTypeName);
            var declared = declaredById.GetValueOrDefault(src.Key) ?? new HashSet<string>(LoopOutputs.Fixed(srcType), StringComparer.Ordinal);

            if (srcType == NodeType.Cleanup)
            {
                errors.Add($"Node {src.Key} ({srcTypeName}) must not have outgoing edges.");
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
                        if (string.IsNullOrWhiteSpace(e.Name))
                        {
                            errors.Add($"Custom edge {e.Id} on node {src.Key} must have a Name.");
                            break;
                        }
                        if (LoopOutputs.IsSuccessOrFailure(e.Name))
                        {
                            errors.Add($"Custom edge {e.Id} on node {src.Key} is named '{e.Name}', which is a fixed output; wire it as an {e.Name} edge instead of a Custom one.");
                            break;
                        }
                        if (!NamedOutputNodeTypes.Contains(srcType))
                        {
                            errors.Add($"Node {src.Key} ({srcTypeName}) has a Custom edge '{e.Name}', but only Human, AI, PR and Condition nodes have named outputs; remove the edge.");
                            break;
                        }
                        if (!declared.Contains(e.Name))
                        {
                            errors.Add($"Node {src.Key} has a Custom edge from output '{e.Name}', which is not declared in outputs; add {{ \"name\": \"{e.Name}\" }} to the node's outputs.");
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

                if (cfg.AiProviderTag?.Trim() is { } tag && AiProviderTag.Problem(tag) is { } tagProblem)
                    errors.Add($"AI node {node.Id} aiProviderTag '{tag}' {tagProblem}.");

                // Session fields are templated, but on a narrower grammar than a
                // prompt: {{Var.<name>}} only. They are checked here rather than
                // in the general placeholder scan below, which accepts every
                // known name — {{PreviousNode.Output}} as a session name would
                // mint a new, unbounded session on every turn.
                foreach (var (field, value) in new[]
                         {
                             ("sessionPlaceholder", cfg.SessionPlaceholder),
                             ("forkFromPlaceholder", cfg.ForkFromPlaceholder),
                         })
                {
                    foreach (var bad in SessionPlaceholderTemplate.DisallowedPlaceholders(value))
                        errors.Add($"AI node {node.Id} {field} may only use {{{{Var.<name>}}}} placeholders; '{{{{{bad}}}}}' is not one.");
                }

                foreach (var rule in cfg.MatchRules ?? new())
                {
                    if (!string.IsNullOrWhiteSpace(rule.EdgeName))
                        CheckOutputReference(node.Id, "AI", "matchRules", rule.EdgeName, declaredById[node.Id], WiredFrom(node.Id), "remove the rule", errors);
                    ValidateMatchRulePattern(node.Id, rule, errors);
                }
            }
            else if (string.Equals(node.NodeType, "Condition", StringComparison.OrdinalIgnoreCase))
            {
                var cfg = NodeConfig.Parse<NodeConfig.Condition>(System.Text.Json.JsonSerializer.Serialize(node.Config));
                var cases = cfg.Cases ?? new List<NodeConfig.ConditionCase>();
                var defaultEdge = (cfg.DefaultEdge ?? string.Empty).Trim();

                conditionTemplates = new List<string> { cfg.Output ?? ConditionNodeExecutor.DefaultTemplate };

                // A Condition routes through its cases and default — never an
                // OnSuccess default.
                if (edges.Any(e => e.SourceNodeId == node.Id
                        && Enum.TryParse<EdgeType>(e.EdgeType, ignoreCase: true, out var role) && role == EdgeType.OnSuccess))
                    errors.Add($"Condition node {node.Id} must not have an OnSuccess edge; it routes via its cases and default edge.");

                // A switch must name its default edge and have at least one case.
                if (defaultEdge.Length == 0)
                    errors.Add($"Condition node {node.Id} must set a default edge.");
                else
                    CheckOutputReference(node.Id, "Condition", "defaultEdge", defaultEdge, declaredById[node.Id], WiredFrom(node.Id), "make a wired output the default edge", errors);
                if (cases.Count == 0)
                    errors.Add($"Condition node {node.Id} must have at least one case.");

                // Names are trimmed, as the executor trims them before routing.
                for (var i = 0; i < cases.Count; i++)
                {
                    var c = cases[i];
                    var edgeName = (c.EdgeName ?? string.Empty).Trim();
                    if (edgeName.Length == 0)
                        errors.Add($"Condition node {node.Id} case {i + 1} must set an edge name.");
                    else
                        CheckOutputReference(node.Id, "Condition", $"case {i + 1}", edgeName, declaredById[node.Id], WiredFrom(node.Id), "remove the case", errors);

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

    /// <summary>
    /// The names a node declares in <c>config.outputs</c>, plus the fixed outputs
    /// of its type. Reports a leftover <c>customEdges</c>, a malformed list, a bad or
    /// repeated name, and outputs the node's type may not have. Names are ordinal, as the engine routes them.
    /// </summary>
    private static HashSet<string> DeclaredOutputs(LoopNodeDto node, List<string> errors)
    {
        var type = LoopTemplateManager.StoredNodeType(node.NodeType);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        const string Example = "{ \"name\": \"approve\" }";

        var config = JsonSerializer.SerializeToNode(node.Config) as JsonObject;
        if (config?.ContainsKey("customEdges") == true)
            errors.Add($"Node {node.Id} has customEdges, which is no longer supported; declare outputs in config.outputs, e.g. [{Example}], and remove customEdges.");

        var outputs = config?["outputs"];
        if (outputs is not null and not JsonArray)
            errors.Add($"Node {node.Id} outputs must be an array of output objects, e.g. [{Example}].");

        var entries = (outputs as JsonArray ?? new JsonArray()).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not JsonObject output)
            {
                errors.Add($"Node {node.Id} outputs entry {i + 1} must be an object with a name, e.g. {Example}.");
                continue;
            }
            var name = LoopOutputs.NameOf(output);
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"Node {node.Id} outputs entry {i + 1} must have a non-empty string \"name\", e.g. {Example}.");
                continue;
            }
            if (!declared.Add(name))
                errors.Add($"Node {node.Id} declares output '{name}' more than once; remove the duplicate from outputs.");
        }

        if (type == NodeType.Cleanup && declared.Count > 0)
            errors.Add($"Cleanup node {node.Id} must not declare outputs ({string.Join(", ", declared.Select(n => $"'{n}'"))}); set outputs to [].");
        else if (type == NodeType.Condition && declared.Contains(LoopOutputs.OnSuccess))
            errors.Add($"Condition node {node.Id} must not declare an OnSuccess output; it routes via its cases and default edge. Remove {{ \"name\": \"OnSuccess\" }} from outputs.");
        else if (!NamedOutputNodeTypes.Contains(type))
            foreach (var name in declared.Where(n => !LoopOutputs.IsSuccessOrFailure(n)))
                errors.Add($"{type} node {node.Id} declares output '{name}', but only Human, AI, PR and Condition nodes can have named outputs; remove it from outputs.");

        declared.UnionWith(LoopOutputs.Fixed(type));
        return declared;
    }

    /// <summary>
    /// A match rule, case or default names the output it routes to; that output
    /// must be one the node declares and has a Custom edge from, and never success
    /// or failure, which are taken by the edge's own type rather than by name.
    /// </summary>
    private static void CheckOutputReference(
        string nodeId, string kind, string field, string name, HashSet<string> declared, HashSet<string> wired,
        string otherFix, List<string> errors)
    {
        if (LoopOutputs.IsSuccessOrFailure(name))
            errors.Add($"{kind} node {nodeId} {field} references '{name}', which is a fixed output and cannot be routed to by name; declare a named output in outputs and reference that instead.");
        else if (!declared.Contains(name))
            errors.Add($"{kind} node {nodeId} {field} references output '{name}', which is not declared in outputs; add {{ \"name\": \"{name}\" }} to outputs.");
        else if (!wired.Contains(name))
            errors.Add($"{kind} node {nodeId} {field} routes to output '{name}', which has no edge; connect '{name}' to a target node or {otherFix}.");
    }
}
