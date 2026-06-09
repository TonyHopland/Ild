using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Destroys the local git state a run owns — its worktree and its per-run
/// branch (ADR-0008). Shared by the retention sweeper and the work-item
/// lifecycle paths (delete / send to Done / send to Backlog) so every way a
/// run dies reclaims the same things. Remote branches and PRs are never
/// touched.
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
