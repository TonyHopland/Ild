using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class CleanupNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cleanup;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var loopRunStore = ctx.Services.GetRequiredService<ILoopRunStore>();
        var repoManager = ctx.Services.GetRequiredService<IRepositoryManager>();
        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id) ?? ctx.Run;

        yield return new NodeOutcome.NodeStarting(JsonSerializer.Serialize(new { nodeType = "Cleanup" }));

        var summary = "no worktree to clean";
        if (!string.IsNullOrEmpty(run.WorktreePath) && Directory.Exists(run.WorktreePath))
        {
            try
            {
                await repoManager.DestroyWorktreeAsync(run.WorktreePath);
                summary = $"worktree removed: {run.WorktreePath}";
            }
            catch (Exception ex)
            {
                summary = $"cleanup failed: {ex.Message}";
            }
            run.WorktreePath = null;
            await loopRunStore.UpdateRunAsync(run);
        }

        yield return new NodeOutcome.Terminal(summary);
    }
}
