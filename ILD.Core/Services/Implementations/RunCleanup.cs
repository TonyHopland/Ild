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
    public static async Task<RunCleanupOutcome> CleanUpAsync(LoopRun run, IRunReclaimer reclaimer, ILoopRunStore runs)
    {
        // Resuming a WaitingHuman run is its expected next step and re-enters at
        // CurrentNodeId without passing through Start, so it would not re-create
        // what this destroys. A finished run's ↻ Retry would — or fails cleanly
        // on a node that needs a worktree.
        if (run.Status is LoopRunStatus.Running or LoopRunStatus.WaitingHuman)
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
