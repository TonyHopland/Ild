using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface ILoopRunStore
{
    Task<LoopRun?> GetByIdAsync(Guid id);
    Task<LoopRun?> GetByPrUrlAsync(string prUrl);
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
