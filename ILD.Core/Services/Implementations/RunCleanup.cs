using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations;

public enum RunCleanupOutcome
{
    Cleaned,
    NotFinished,
    ReclaimFailed,
    RunGone,
}

/// <summary>
/// Run Cleanup: reclaim a finished run's worktree, local branch and AI session
/// snapshots while keeping the run row and its history — the non-destructive
/// counterpart of deleting the run. Shared by the per-run cleanup on the Runs tab
/// and the <see cref="FinishedRunCleaner"/>, which removes a run's local state
/// when it ends and as the retention sweeper's backstop.
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

    /// <summary>
    /// Whether the run has ended: Completed or Cancelled. Narrower than
    /// <see cref="IsFinished"/>, which also counts Failed: a person may clean up a
    /// Failed run by hand, but it has not ended — it can still be retried — so its
    /// local state is not removed on its own and its history has no end date.
    /// </summary>
    public static bool HasEnded(LoopRun run)
        => run.Status is LoopRunStatus.Completed or LoopRunStatus.Cancelled;

    /// <summary>
    /// Whether the engine still drives the run, so nothing may touch its worktree.
    /// The engine is looked up here rather than injected, because it ends runs
    /// through the <see cref="FinishedRunCleaner"/>; false where there is none.
    /// </summary>
    public static async Task<bool> IsDrivenByEngineAsync(IServiceProvider services, Guid runId)
        => services.GetService<ILoopEngine>() is { } engine && (await engine.GetActiveRunIdsAsync()).Contains(runId);

    public static async Task<RunCleanupOutcome> CleanUpAsync(
        LoopRun run, IRunReclaimer reclaimer, ILoopRunStore runs, IAdapterSessionSnapshotStore snapshots)
    {
        if (!IsFinished(run))
            return RunCleanupOutcome.NotFinished;

        // Nothing resumes a finished run's sessions, so they go whether or not the
        // git state can be removed this time.
        await snapshots.DeleteForRunAsync(run.Id);

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
