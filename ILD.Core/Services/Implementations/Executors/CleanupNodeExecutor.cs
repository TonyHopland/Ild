using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using System.Text.Json;

namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Marks the run finished. Deliberately does <b>not</b> destroy the worktree:
/// each run keeps its own worktree and branch so a completed run stays
/// inspectable afterwards. Disk is reclaimed later by the
/// <see cref="ILD.Core.Services.Implementations.WorktreeRetentionSweeper"/>
/// (or an explicit reset / retained-run un-pin). See ADR-0008.
/// </summary>
public sealed class CleanupNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cleanup;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        yield return new NodeOutcome.NodeStarting(JsonSerializer.Serialize(new { nodeType = "Cleanup" }));

        var summary = string.IsNullOrEmpty(ctx.Run.WorktreePath)
            ? "no worktree"
            : $"worktree retained for inspection: {ctx.Run.WorktreePath}";

        yield return new NodeOutcome.Terminal(summary);
    }
}
