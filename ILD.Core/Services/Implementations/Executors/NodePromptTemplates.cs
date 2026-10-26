using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// The prompt templates a node renders on its next visit, as its executor
/// decides them, so the engine can check the global variables they use before
/// the node starts.
/// </summary>
public static class NodePromptTemplates
{
    /// <summary>
    /// The global-variable names the node's templates use on this visit,
    /// distinct ignoring case, in order of first appearance.
    /// </summary>
    public static IReadOnlyList<string> GlobalVariableNames(LoopNode node, LoopRun run)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return For(node, run)
            .SelectMany(PromptPlaceholderRegistry.GlobalVariableNames)
            .Where(seen.Add)
            .ToList();
    }

    /// <summary>The <paramref name="names"/> with no variable (ignoring case) or an empty value.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string> names, IEnumerable<GlobalVariable> all)
    {
        var set = all.Where(g => g.Value.Length > 0)
            .Select(g => g.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Where(n => !set.Contains(n)).ToList();
    }

    private static IEnumerable<string?> For(LoopNode node, LoopRun run)
    {
        // Re-entry after a signal finishes the visit that already started.
        if (run.ExternalActionResult is not null) return [];

        switch (node.NodeType)
        {
            case NodeType.AI:
                // Steering into the live session sends the human's note, not the prompt.
                var steering = run.SteeringNote is not null && !string.IsNullOrWhiteSpace(run.CurrentAiSessionId);
                return steering ? [] : [NodeConfig.Parse<NodeConfig.Ai>(node.Config).Prompt];
            case NodeType.Human:
                return [NodeConfig.Parse<NodeConfig.Human>(node.Config).Prompt];
            case NodeType.Prompt:
                return [NodeConfig.Parse<NodeConfig.Prompt>(node.Config).Template];
            case NodeType.SetVariable:
                return (NodeConfig.Parse<NodeConfig.SetVariable>(node.Config).Assignments ?? []).Select(a => a.Value);
            case NodeType.PR:
                var pr = NodeConfig.Parse<NodeConfig.Pr>(node.Config);
                // The description is rendered only to open the pull request.
                return string.IsNullOrEmpty(run.PrUrl) ? [pr.Prompt, pr.PrDescriptionTemplate] : [pr.Prompt];
            case NodeType.Condition:
                var condition = NodeConfig.Parse<NodeConfig.Condition>(node.Config);
                var subjects = (condition.Cases ?? [])
                    .Where(c => string.Equals((c.Variant ?? "").Trim(), "TextMatches", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Subject ?? ConditionNodeExecutor.DefaultTemplate);
                return subjects.Prepend(condition.Output ?? ConditionNodeExecutor.DefaultTemplate);
            default:
                return [];
        }
    }
}
