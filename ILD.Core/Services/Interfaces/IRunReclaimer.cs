using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Destroys the local git state a run owns — its worktree and its per-run
/// branch (ADR-0008). Every path that takes a run's worktree away comes
/// through here: the retention sweeper, manual deletion (the run delete
/// endpoint, or deleting the whole work item), and manual cleanup, which is
/// the one that keeps the run row. Remote branches and PRs are never touched.
/// </summary>
public interface IRunReclaimer
{
    /// <summary>
    /// Best-effort reclaim of the run's worktree and local branch. Stops any
    /// Worktree Preview running on the worktree first, since the preview
    /// would otherwise be left holding its ports with no path left to address
    /// its stop control by. Returns true only when all reachable local git
    /// state is verified gone — callers should keep the run row, and whatever
    /// points at that state, when this returns false so a later retention
    /// sweep can retry the reclaim.
    /// </summary>
    Task<bool> ReclaimLocalStateAsync(LoopRun run);
}
