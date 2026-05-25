using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Pure I/O human-feedback node: renders the prompt, then either parks
/// the run (first entry, no external action result) or routes based on
/// the captured response (re-entry after webhook/API signal).
/// </summary>
public sealed class HumanNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Human;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Human>(ctx.Node.Config);
        var template = cfg.Prompt ?? string.Empty;
        var rendering = ctx.Services.GetService<IPromptRenderingService>();
        var workItems = ctx.Services.GetRequiredService<IWorkItemManager>();
        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        string rendered = template;
        if (rendering != null && wi != null)
            rendered = await rendering.RenderAsync(template, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput);

        if (ctx.Run.ExternalActionResult is null)
        {
            // First entry: announce work, then park.
            yield return new NodeOutcome.NodeStarting(rendered);
            yield return new NodeOutcome.WaitingAction(HumanFeedbackReasons.HumanInputNeeded, rendered);
            yield break;
        }

        // Re-entry: external signal arrived. Skip NodeStarting to avoid creating
        // a second LoopRunNode — the existing WaitingHuman node covers this visit.
        switch (ctx.Run.ExternalActionResultType)
        {
            case ExternalActionResultType.Reject:
                yield return new NodeOutcome.Fail(EdgeType.OnFailure, "Rejected", ctx.Run.ExternalActionResult);
                yield break;
            case ExternalActionResultType.Respond:
                yield return new NodeOutcome.Success(EdgeType.OnRespond, ctx.Run.ExternalActionResult);
                yield break;
            default:
                yield return new NodeOutcome.Success(EdgeType.OnSuccess, ctx.Run.ExternalActionResult);
                yield break;
        }
    }
}
