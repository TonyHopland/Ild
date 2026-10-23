namespace ILD.Core.Services.Interfaces;

/// <summary>
/// End-of-run removal: once a run has ended (Completed or Cancelled) and no loop
/// drives it, its worktree, local branch and AI session snapshots go and the run
/// row, history included, stays until retention deletes it. Called when a run
/// ends by any route, and by the retention sweeper as a backstop for whatever a
/// finished run left behind. Pinned runs are never touched.
/// </summary>
public interface IFinishedRunCleaner
{
    /// <summary>
    /// Remove the run's local state through <c>RunCleanup</c>. Touches nothing,
    /// and returns false, while the engine still drives the run, or when it is
    /// gone, pinned, or has not ended; a Failed run counts as ended only with
    /// <paramref name="allowFailed"/>. A second call while one is in flight for
    /// the same run returns false at once, and the one in flight then tries again
    /// unless it already removed everything, so a call made once the run ended
    /// or was let go is never lost. True when the run's local state was
    /// removed; a failure is logged and leaves the run's pointers for a later
    /// attempt. Never throws.
    /// </summary>
    Task<bool> RemoveLocalStateAsync(Guid runId, bool allowFailed = false);
}
