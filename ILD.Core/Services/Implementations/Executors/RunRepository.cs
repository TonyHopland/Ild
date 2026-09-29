using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.Executors;

public static class RunRepository
{
    /// <summary>
    /// The repository a run works in: the one pinned on the run, read straight off
    /// the run in hand; the work item's is only a fallback for a run that has none.
    /// </summary>
    public static Guid? IdOf(LoopRun run, WorkItemView? workItem) => run.RepositoryId ?? workItem?.RepositoryId;

    /// <inheritdoc cref="IdOf"/>
    /// <remarks>Reads the work item only when the run pins no repository.</remarks>
    public static async Task<Guid?> IdOfAsync(LoopRun run, IWorkItemManager workItems)
        => run.RepositoryId ?? (await workItems.GetWorkItemAsync(run.WorkItemId))?.RepositoryId;
}
