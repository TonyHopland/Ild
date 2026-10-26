using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Assigns loop variables from templated values. It never invokes AI, runs a
/// command, or touches the worktree. Every value is rendered before the engine
/// writes any of them, so all of them see the variables as they were before the
/// node ran. Its Output is its input, unchanged.
/// </summary>
public sealed class SetVariableNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.SetVariable;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.SetVariable>(ctx.Node.Config);
        var input = ctx.Run.PreviousNodeOutput;
        yield return new NodeOutcome.NodeStarting(input);

        var wi = await ctx.Services.GetRequiredService<IWorkItemManager>().GetWorkItemAsync(ctx.Run.WorkItemId);
        if (wi is null)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "WorkItem not found");
            yield break;
        }

        var rendering = ctx.Services.GetService<IPromptRenderingService>();
        var assignments = new List<VariableAssignment>();
        foreach (var a in cfg.Assignments ?? [])
        {
            var value = a.Value ?? string.Empty;
            if (rendering is not null)
                value = await rendering.RenderAsync(value, ctx.Run.Id, wi, input);
            assignments.Add(new VariableAssignment(a.Name ?? string.Empty, value));
        }

        yield return new NodeOutcome.VariablesAssigned(assignments);
        yield return new NodeOutcome.Success(EdgeType.OnSuccess, input);
    }
}
