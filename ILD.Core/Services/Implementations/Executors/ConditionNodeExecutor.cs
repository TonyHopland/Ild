using System.Text.RegularExpressions;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Evaluates a single predicate against run/work-item state and routes to a
/// fixed <c>true</c> or <c>false</c> custom edge. It never invokes AI, runs a
/// command, or touches the worktree. The pass-through <c>Output</c> is emitted
/// identically on both branches; an evaluation error routes to OnFailure.
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
        var workItems = ctx.Services.GetRequiredService<IWorkItemManager>();
        var rendering = ctx.Services.GetService<IPromptRenderingService>();

        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        if (wi is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "WorkItem not found");
            yield break;
        }

        // Output is pass-through by default and identical on both branches, so
        // render it once before evaluating the predicate.
        var output = await RenderAsync(rendering, cfg.Output ?? DefaultTemplate, ctx, wi);

        yield return new NodeOutcome.NodeStarting(output);

        var (matched, error) = await EvaluateAsync(cfg, ctx, wi, rendering);
        if (error is not null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, error);
            yield break;
        }

        yield return new NodeOutcome.Success(EdgeType.Custom, output, matched ? "true" : "false");
    }

    /// <summary>
    /// Resolve the predicate to a boolean, or return an evaluation error that
    /// routes the node to OnFailure. Kept out of the iterator so the regex
    /// try/catch can live in a method that may catch (iterators forbid that).
    /// </summary>
    private static async Task<(bool Matched, string? Error)> EvaluateAsync(
        NodeConfig.Condition cfg,
        NodeExecutionContext ctx,
        WorkItemView wi,
        IPromptRenderingService? rendering)
    {
        var variant = (cfg.Variant ?? string.Empty).Trim();

        if (string.Equals(variant, "TextMatches", StringComparison.OrdinalIgnoreCase))
        {
            var subject = await RenderAsync(rendering, cfg.Subject ?? DefaultTemplate, ctx, wi);
            try
            {
                return (Regex.IsMatch(subject, cfg.Pattern ?? string.Empty, MatchOptions), null);
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
            var tag = cfg.Tag;
            var hasTag = !string.IsNullOrWhiteSpace(tag)
                && wi.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            return (hasTag, null);
        }

        return (false, $"Unknown condition variant '{cfg.Variant}'");
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
