using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface ILoopRunStore
{
    Task<LoopRun?> GetByIdAsync(Guid id);
    Task<LoopRun?> GetByPrUrlAsync(string prUrl);

    /// <summary>
    /// The most recent run whose isolated worktree is at <paramref name="worktreePath"/>.
    /// Each run gets its own worktree (ADR-0008), so this resolves a worktree path
    /// back to the run — and through it the work item and repository — for callers
    /// that only hold the path (e.g. the agent tool surface). Null when no run owns
    /// that path.
    /// </summary>
    Task<LoopRun?> GetByWorktreePathAsync(string worktreePath);

    Task<LoopRun?> GetByWorkItemAsync(string workItemId);
    Task<IReadOnlyList<LoopRun>> GetAllByWorkItemAsync(string workItemId);
    Task<IReadOnlyList<LoopRun>> GetByWorkItemPagedAsync(string workItemId, int skip, int take);
    Task<LoopRun?> GetCurrentByWorkItemAsync(string workItemId);

    /// <summary>
    /// The work item's single active run, if any: the most recent run whose
    /// status the engine considers alive (<c>Running</c> or <c>WaitingHuman</c>).
    /// Used to enforce the at-most-one-active-run-per-work-item invariant.
    /// </summary>
    Task<LoopRun?> GetActiveByWorkItemAsync(string workItemId);

    Task<IReadOnlyList<LoopRun>> GetAllAsync(int skip = 0, int take = 100);
    Task<IReadOnlyList<LoopRun>> GetRunningRunsAsync();

    /// <summary>
    /// Runs the engine considers alive: <c>Running</c> plus <c>WaitingHuman</c>
    /// (parked at a Human/PR node awaiting a signal).
    /// </summary>
    Task<IReadOnlyList<LoopRun>> GetActiveRunsAsync();

    /// <summary>
    /// The Active Work Item Set: the work items this instance is currently
    /// working on, one per run <see cref="GetActiveRunsAsync"/> considers alive.
    /// The scheduler derives it fresh on every poll pass rather than maintaining
    /// it incrementally, so a run that ended releases its work item here
    /// whatever status the item itself landed in, and no terminal path has to
    /// remember to say so. That one set is both the concurrency gate and the
    /// heartbeat the work-item server's stale reclaimer keys off: an item whose
    /// run died locally stops being heartbeated, so the server can reclaim it,
    /// and its slot comes back on the very next pass.
    ///
    /// Projected on purpose, not filtered from <see cref="GetActiveRunsAsync"/>.
    /// The caller wants one string column, not the run graphs — and materialising
    /// those every pass would put them in the scope's change tracker, so a later
    /// re-read of the same run inside the pass would resolve to the top-of-pass
    /// snapshot instead of the database.
    /// </summary>
    Task<IReadOnlyList<string>> GetActiveWorkItemIdsAsync();

    /// <summary>
    /// Mark <paramref name="run"/> cancelled, with a completion timestamp and
    /// <paramref name="reason"/> recorded. For when the work-item server has
    /// moved on from the run's item — finished it, reset it, or deleted it — so
    /// the run has nothing left to do: while its row still says alive it keeps
    /// the item in the Active Work Item Set, heartbeated and holding a
    /// concurrency slot nothing will ever release. A run mid-node stops on its
    /// own, since the engine re-reads this row each iteration.
    ///
    /// Deliberately not <c>ILoopEngine.CancelRunAsync</c>, which hands the work
    /// item back to HumanFeedback — that would undo the very server state being
    /// reacted to.
    /// </summary>
    Task MarkRunCancelledAsync(LoopRun run, string reason);

    /// <summary>
    /// Terminal runs (Completed/Failed/Cancelled) that completed before
    /// <paramref name="cutoff"/> and are not pinned (<c>Retain == false</c>).
    /// Candidates for the worktree retention sweeper; the caller still applies
    /// the "not the work item's current run" rule. Bounded by <paramref name="take"/>.
    /// </summary>
    Task<IReadOnlyList<LoopRun>> GetReclaimableRunsAsync(DateTime cutoff, int take = 200);

    /// <summary>
    /// Runs parked at a PR node awaiting merge: <c>WaitingHuman</c> with a
    /// <c>PrUrl</c> set and <c>HumanFeedbackReason</c> =
    /// <see cref="ILD.Data.Enums.HumanFeedbackReasons.PrAwaitingMerge"/>. Backs
    /// the PR heartbeat poller.
    /// </summary>
    Task<IReadOnlyList<LoopRun>> GetPrAwaitingMergeRunsAsync();

    Task<IReadOnlyList<LoopRunNode>> GetRunNodesAsync(Guid runId);
    /// <summary>Run nodes for a run with their <c>LoopNode</c> eager-loaded (left join — may be null if the template node was since removed).</summary>
    Task<IReadOnlyList<LoopRunNode>> GetRunNodesWithNodeAsync(Guid runId);
    Task<IReadOnlyList<AdapterSessionSnapshot>> GetSessionSnapshotsAsync(Guid runId);
    Task<IReadOnlyList<LoopRunSessionBinding>> GetSessionBindingsAsync(Guid runId);
    Task<LoopRunSessionBinding?> GetSessionBindingAsync(Guid runId, string adapterName, string placeholderId);
    Task UpsertSessionBindingAsync(Guid runId, string adapterName, string placeholderId, string sessionId);

    /// <summary>
    /// All loop variables for a run, ordered by name. Returns an empty list when
    /// the run has none. Backs <c>{{Var.&lt;name&gt;}}</c> placeholder rendering and
    /// the agent variable-listing endpoint.
    /// </summary>
    Task<IReadOnlyList<LoopRunVariable>> GetVariablesAsync(Guid runId);

    /// <summary>
    /// Create or overwrite a single loop variable by (runId, name), touching only
    /// that row so concurrent writes to other variables / control-plane columns
    /// are never clobbered.
    /// </summary>
    Task SetVariableAsync(Guid runId, string name, string value);
    Task<LoopRunNode?> GetRunNodeAsync(Guid runId, Guid nodeId);
    Task<LoopRunNode?> GetRunNodeByIdAsync(Guid runNodeId);
    Task CreateRunAsync(LoopRun run);
    Task UpdateRunAsync(LoopRun run);

    /// <summary>
    /// Atomically persist the live AI session id captured mid-stream, touching
    /// only that column. Used by the AI node executor's <c>OnSessionId</c>
    /// callback, which runs on the adapter's stream task in its own DI scope —
    /// a single-column write avoids clobbering concurrent control-plane writes
    /// (halt, pause, cancel) on the same run.
    /// </summary>
    Task SetCurrentAiSessionIdAsync(Guid runId, string sessionId);

    /// <summary>
    /// Atomically clear the one-shot steering note after the AI node has
    /// consumed it, touching only that column so a concurrent control-plane
    /// write is not lost.
    /// </summary>
    Task ClearSteeringNoteAsync(Guid runId);

    /// <summary>
    /// Refresh a tracked <see cref="LoopRun"/> instance with the row's current
    /// column values, discarding unsaved in-memory changes. Used by the engine
    /// before persisting so a stale instance held across a long node execution
    /// cannot clobber concurrent control-plane writes (pause, cancel, pin).
    /// </summary>
    Task ReloadAsync(LoopRun run);
    Task CreateRunNodeAsync(LoopRunNode runNode);
    Task UpdateRunNodeAsync(LoopRunNode runNode);
    Task DeleteRunNodeAsync(Guid runNodeId);

    /// <summary>
    /// Transition every <see cref="LoopRunNodeStatus.Running"/> node of a run to
    /// <see cref="LoopRunNodeStatus.Interrupted"/>, stamping <c>CompletedAt</c>,
    /// and return the nodes that were changed. Enforces the "at most one Running
    /// node per run" invariant at the two points a run's driver changes hands:
    /// when a new driver (re)enters the loop and when the reconciler finalizes an
    /// orphaned run. A run stopped cleanly has no Running node, so this is a no-op
    /// on the happy path (see issue #39).
    /// </summary>
    Task<IReadOnlyList<LoopRunNode>> InterruptRunningNodesAsync(Guid runId);
    Task<LoopNode?> GetStartNodeAsync(Guid versionId);
    Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId);
    Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForNodeIdsAsync(IReadOnlyList<Guid> nodeIds);
    Task<bool> HasFailureEdgeAsync(Guid nodeId);
    Task<LoopNodeEdge?> GetEdgeAsync(Guid edgeId);
    Task<IReadOnlyList<Guid>> GetFailedRunIdsAsync();

    /// <summary>
    /// Atomically increments and returns the next per-run event log sequence
    /// number. Replaces the previous global lock + MAX(Sequence) scan.
    /// </summary>
    Task<int> AllocateNextEventSequenceAsync(Guid runId);

    /// <summary>
    /// Hard-deletes a loop run and all of its dependent rows (run nodes,
    /// edge traversals, event log entries). Returns false if the run does
    /// not exist or is still Running.
    /// </summary>
    Task<bool> DeleteAsync(Guid runId);
}
