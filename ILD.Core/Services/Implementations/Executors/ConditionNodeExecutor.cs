using System.Text.RegularExpressions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// A switch over run/work-item state: an ordered list of predicate cases each
/// routing to a named custom edge, plus a default edge taken when no case
/// matches. It never invokes AI, runs a command, or touches the worktree. The
/// pass-through <c>Output</c> is emitted identically on every branch; an
/// evaluation error routes to OnFailure. Legacy true/false conditions are read
/// as a single <c>true</c> case with a <c>false</c> default (see
/// <see cref="NormalizeCases"/>).
/// </summary>
public sealed class ConditionNodeExecutor : INodeExecutor
{
    public const string DefaultTemplate = "{{Node.Input}}";

    // The edge names a legacy (pre-switch) true/false Condition routed through.
    // Kept so old loops route identically without a config rewrite.
    public const string LegacyMatchEdge = "true";
    public const string LegacyDefaultEdge = "false";

    // Same regex options as AI MatchRules: case-insensitive single-line match.
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase;

    public NodeType NodeType => NodeType.Condition;

    /// <summary>
    /// Resolves a Condition config to its effective switch: the ordered cases
    /// and the default edge. A config with explicit <see cref="NodeConfig.Condition.Cases"/>
    /// is used as-is (its default edge falling back to <see cref="LegacyDefaultEdge"/>
    /// only when unset); an old true/false config is read as a single case
    /// routing to <see cref="LegacyMatchEdge"/> with <see cref="LegacyDefaultEdge"/>
    /// as the default — identical routing to the pre-switch executor.
    /// </summary>
    internal static (IReadOnlyList<NodeConfig.ConditionCase> Cases, string DefaultEdge) NormalizeCases(
        NodeConfig.Condition cfg)
    {
        if (cfg.Cases is { Count: > 0 })
        {
            var defaultEdge = string.IsNullOrWhiteSpace(cfg.DefaultEdge)
                ? LegacyDefaultEdge
                : cfg.DefaultEdge.Trim();
            return (cfg.Cases, defaultEdge);
        }

        var legacy = new NodeConfig.ConditionCase
        {
            Variant = cfg.Variant,
            Subject = cfg.Subject,
            Pattern = cfg.Pattern,
            Tag = cfg.Tag,
            EdgeName = LegacyMatchEdge,
        };
        return (new[] { legacy }, LegacyDefaultEdge);
    }

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Condition>(ctx.Node.Config);
        var (cases, defaultEdge) = NormalizeCases(cfg);
        var workItems = ctx.Services.GetRequiredService<IWorkItemManager>();
        var rendering = ctx.Services.GetService<IPromptRenderingService>();

        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        if (wi is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "WorkItem not found");
            yield break;
        }

        // Output is pass-through by default and identical on every branch, so
        // render it once before evaluating the cases.
        var output = await RenderAsync(rendering, cfg.Output ?? DefaultTemplate, ctx, wi);

        yield return new NodeOutcome.NodeStarting(output);

        // First case whose predicate holds wins; no match takes the default edge.
        string? edgeName = null;
        foreach (var c in cases)
        {
            var (matched, error) = await EvaluateAsync(c, ctx, wi, rendering);
            if (error is not null)
            {
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, error);
                yield break;
            }
            if (matched)
            {
                edgeName = c.EdgeName?.Trim();
                break;
            }
        }

        edgeName = string.IsNullOrWhiteSpace(edgeName) ? defaultEdge : edgeName;
        yield return new NodeOutcome.Success(EdgeType.Custom, output, edgeName);
    }

    /// <summary>
    /// Resolve a single case's predicate to a boolean, or return an evaluation
    /// error that routes the node to OnFailure. Kept out of the iterator so the
    /// regex try/catch can live in a method that may catch (iterators forbid that).
    /// </summary>
    private static async Task<(bool Matched, string? Error)> EvaluateAsync(
        NodeConfig.ConditionCase c,
        NodeExecutionContext ctx,
        WorkItemView wi,
        IPromptRenderingService? rendering)
    {
        var variant = (c.Variant ?? string.Empty).Trim();

        if (string.Equals(variant, "TextMatches", StringComparison.OrdinalIgnoreCase))
        {
            var subject = await RenderAsync(rendering, c.Subject ?? DefaultTemplate, ctx, wi);
            try
            {
                return (Regex.IsMatch(subject, c.Pattern ?? string.Empty, MatchOptions), null);
            }
            catch (ArgumentException ex)
            {
                return (false, $"Invalid regex pattern: {ex.Message}");
            }
        }

        if (string.Equals(variant, "PrExists", StringComparison.OrdinalIgnoreCase))
            return (!string.IsNullOrWhiteSpace(ctx.Run.PrUrl), null);

        if (string.Equals(variant, "HasTag", StringComparison.OrdinalIgnoreCase))
        {
            var tag = c.Tag;
            var hasTag = !string.IsNullOrWhiteSpace(tag)
                && wi.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            return (hasTag, null);
        }

        return (false, $"Unknown condition variant '{c.Variant}'");
    }

    private static async Task<string> RenderAsync(
        IPromptRenderingService? rendering,
        string template,
        NodeExecutionContext ctx,
        WorkItemView wi)
        => rendering is null
            ? template
            : await rendering.RenderAsync(template, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput);
}
