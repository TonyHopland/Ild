using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Destroys the local git state a run owns — its worktree and its per-run
/// branch (ADR-0008). That state lives exactly as long as the run row, so
/// this is invoked only by the paths that delete the run: the retention
/// sweeper and manual deletion (the run delete endpoint, or deleting the
/// whole work item). Remote branches and PRs are never touched.
/// </summary>
public interface IRunReclaimer
{
    /// <summary>
    /// Best-effort reclaim of the run's worktree and local branch. Returns
    /// true only when all reachable local git state is verified gone —
    /// callers should keep the run row when this returns false so a later
    /// retention sweep can retry the reclaim.
    /// </summary>
    Task<bool> ReclaimLocalStateAsync(LoopRun run);
}
