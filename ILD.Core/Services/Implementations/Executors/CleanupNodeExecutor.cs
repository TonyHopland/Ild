using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class CleanupNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cleanup;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var repoManager = ctx.Services.GetRequiredService<IRepositoryManager>();

        yield return new NodeOutcome.NodeStarting(JsonSerializer.Serialize(new { nodeType = "Cleanup" }));

        var summary = "no worktree to clean";
        if (!string.IsNullOrEmpty(ctx.Run.WorktreePath) && Directory.Exists(ctx.Run.WorktreePath))
        {
            try
            {
                await repoManager.DestroyWorktreeAsync(ctx.Run.WorktreePath);
                summary = $"worktree removed: {ctx.Run.WorktreePath}";
            }
            catch (Exception ex)
            {
                summary = $"cleanup failed: {ex.Message}";
            }
            yield return new NodeOutcome.WorktreeDestroyed();
        }

        yield return new NodeOutcome.Terminal(summary);
    }
}
