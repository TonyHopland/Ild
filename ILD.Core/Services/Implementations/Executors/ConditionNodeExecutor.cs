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
/// evaluation error routes to OnFailure. Pre-switch true/false conditions are
/// upgraded to this shape by the one-time <c>ConditionSwitchMigrator</c>; the
/// executor only ever reads the switch config.
/// </summary>
public sealed class ConditionNodeExecutor : INodeExecutor
{
    public const string DefaultTemplate = "{{Node.Input}}";

    // Same regex options as AI MatchRules: case-insensitive single-line match.
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase;

    public NodeType NodeType => NodeType.Condition;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Condition>(ctx.Node.Config);
        var cases = cfg.Cases ?? new List<NodeConfig.ConditionCase>();
        var defaultEdge = (cfg.DefaultEdge ?? string.Empty).Trim();
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
        var matchedAny = false;
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
                matchedAny = true;
                break;
            }
        }

        if (!matchedAny)
            edgeName = defaultEdge;

        if (string.IsNullOrWhiteSpace(edgeName))
        {
            // A blank edge name (an empty switch, no default edge, or a matched
            // case that named no edge) would emit Success(Custom, "") — which the
            // engine treats as a run terminus and silently completes. Fail onto
            // OnFailure instead so the misconfiguration surfaces. Save-time
            // validation already forbids this shape.
            yield return new NodeOutcome.Fail(
                EdgeType.OnFailure,
                matchedAny
                    ? "Condition matched a case with no edge name"
                    : "Condition matched no case and has no default edge");
            yield break;
        }

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
