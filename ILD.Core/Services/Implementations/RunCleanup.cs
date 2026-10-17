using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public enum RunCleanupOutcome
{
    Cleaned,
    NotFinished,
    ReclaimFailed,
    RunGone,
}

/// <summary>
/// Run Cleanup: reclaim a finished run's worktree and local branch while
/// keeping the run row and its history — the non-destructive counterpart of
/// deleting the run. Shared by the per-run cleanup on the Runs tab and the bulk
/// cleanup on Settings &gt; Storage.
/// </summary>
public static class RunCleanup
{
    /// <summary>
    /// Whether the run is done with its worktree: Completed, Failed or
    /// Cancelled. Not a parked WaitingHuman run, whose resume re-enters at
    /// CurrentNodeId without passing through Start, so nothing would re-create
    /// what a cleanup destroys; a finished run's ↻ Retry would — or fails cleanly
    /// on a node that needs a worktree. Listed rather than excluded, so a status
    /// added later is never cleaned up until it is put here.
    /// </summary>
    public static bool IsFinished(LoopRun run)
        => run.Status is LoopRunStatus.Completed or LoopRunStatus.Failed or LoopRunStatus.Cancelled;

    public static async Task<RunCleanupOutcome> CleanUpAsync(LoopRun run, IRunReclaimer reclaimer, ILoopRunStore runs)
    {
        if (!IsFinished(run))
            return RunCleanupOutcome.NotFinished;

        if (!await reclaimer.ReclaimLocalStateAsync(run))
            return RunCleanupOutcome.ReclaimFailed;

        // The row now outlives the git state it names, so the pointers go with
        // it: a surviving BranchName would keep claiming a branch that is free,
        // and a surviving WorktreePath would name a directory that is gone.
        if (!await runs.ClearLocalGitStateAsync(run.Id))
            return RunCleanupOutcome.RunGone;
        return RunCleanupOutcome.Cleaned;
    }
}
