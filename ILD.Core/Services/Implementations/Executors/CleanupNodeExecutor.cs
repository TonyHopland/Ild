using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using System.Text.Json;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Marks the run finished. The node itself destroys nothing: the run's
/// worktree, local branch and session data are removed by the
/// <see cref="IFinishedRunCleaner"/> once the engine's loop for the run has
/// exited, unless the run is pinned. The run row and its history stay until
/// the <see cref="ILD.Core.Services.Implementations.WorktreeRetentionSweeper"/>
/// or a manual run delete removes them. See ADR-0008.
/// </summary>
public sealed class CleanupNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cleanup;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        yield return new NodeOutcome.NodeStarting(JsonSerializer.Serialize(new { nodeType = "Cleanup" }));

        var summary = string.IsNullOrEmpty(ctx.Run.WorktreePath)
            ? "no worktree"
            : ctx.Run.Retain
                ? "run finished; pinned, so its worktree and session data are kept"
                : "run finished; its worktree and session data are removed as it ends";

        yield return new NodeOutcome.Terminal(summary);
    }
}
