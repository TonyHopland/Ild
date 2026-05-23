using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class PromptNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Prompt;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Prompt>(ctx.Node.Config);
        var template = cfg.Template ?? string.Empty;
        var workItems = ctx.Services.GetRequiredService<IWorkItemManager>();
        var rendering = ctx.Services.GetService<IPromptRenderingService>();
        var wi = await workItems.GetWorkItemAsync(ctx.Run.WorkItemId);
        string rendered;
        if (rendering != null && wi != null)
            rendered = await rendering.RenderAsync(template, ctx.Run.Id, wi, ctx.Run.PreviousNodeOutput);
        else
            rendered = template;

        yield return new NodeOutcome.NodeStarting(rendered);
        yield return new NodeOutcome.Success(EdgeType.OnSuccess, rendered);
    }
}
