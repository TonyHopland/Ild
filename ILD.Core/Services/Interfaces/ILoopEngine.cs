using ILD.Data.DTOs;
using Microsoft.Extensions.Logging;
using ILD.Data.Enums;
using ILD.Data.Entities;
namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Result of an external event for a node parked in <c>WaitingHuman</c>.
/// </summary>
public sealed record NodeSignal(ExternalActionResultType Type, string? Output = null, string? Error = null, string? EdgeName = null)
{
    public static NodeSignal Success(string? output = null) => new(ExternalActionResultType.Success, output);
    public static NodeSignal Reject(string error, string? output = null) => new(ExternalActionResultType.Reject, output, error);

    /// <summary>Route to a node's named custom edge (e.g. a Human node button).</summary>
    public static NodeSignal Custom(string edgeName, string? output = null) => new(ExternalActionResultType.Success, output, EdgeName: edgeName);

    /// <summary>Back-compat alias: the historical "respond" outlet is the custom edge named "Respond".</summary>
    public static NodeSignal Respond(string? output = null) => Custom("Respond", output);
}

public interface ILoopEngine
{
    Task StartRunAsync(string workItemId, CancellationToken cancellationToken = default);
    Task PauseRunAsync(Guid runId);
    Task ResumeRunAsync(Guid runId);

    /// <summary>
    /// End a run and nothing else: stop the in-flight node, write the row
    /// terminal with <paramref name="reason"/>, and tell the UI. How a run gets
    /// ended by the cancel button, by a human sending the work item to Done, by
    /// a poll pass reacting to a Done the server already holds, and by startup
    /// reconcile.
    ///
    /// Not a choke point, and nothing has to funnel through here. Other writers
    /// end runs their own way, on purpose: the engine's own lifecycle finishes
    /// its runs Completed at a Cleanup node or Failed on a node failure or
    /// crash; <c>StuckRunWatchdog</c> marks a driverless "Completed yet Running"
    /// row Failed, deliberately keeping the completion timestamp it was
    /// finalized with; and <c>WorkItemManager</c> writes Completed on the
    /// Cleanup→Backlog path and Cancelled as its no-engine fallback. Routing any
    /// of those through here would overwrite the outcome each one means to
    /// record.
    ///
    /// That is the point of the Active Work Item Set being derived: a run stops
    /// holding its work item's concurrency slot as soon as its row leaves
    /// <c>LoopRunStore.IsAlive</c>, whichever writer got there first, so no
    /// terminal path has to remember to release anything.
    ///
    /// What the work item should say afterwards is deliberately the caller's
    /// business: a human finishing it wants Done, the cancel button wants
    /// HumanFeedback, and a poll pass reacting to the server wants to leave
    /// alone the status the server already has. Callers that need a disposition
    /// apply it themselves rather than inheriting one they have to overwrite.
    ///
    /// Returns that run's work item id — the item whose concurrency slot has
    /// just come back, and the one a caller is about to give a status of its
    /// own — or <c>null</c> if there is no such run. Handing it back is what
    /// lets a caller apply its disposition without loading the run itself: an
    /// entity it tracked before this wrote the row would be stale, and writing
    /// through it puts the run back the way it was.
    ///
    /// Idempotent: a run that has already ended stays exactly as it ended.
    /// </summary>
    Task<string?> StopRunAsync(Guid runId, string reason);

    /// <summary>
    /// <see cref="StopRunAsync"/>, then park the work item in HumanFeedback for
    /// a human to pick up — what the UI's cancel button means.
    /// </summary>
    Task CancelRunAsync(Guid runId);

    /// <summary>
    /// Halt the in-flight AI node of an actively-running run: kill the agent
    /// process now (cancel the run's CTS) and park the run at
    /// <c>WaitingHuman</c> with <c>IsHalted</c> set, keeping <c>CurrentNodeId</c>
    /// so it can be resumed against the same session. No-op unless the run is
    /// <c>Running</c> on an AI node (guards the halt-races-completion case).
    /// </summary>
    Task HaltRunAsync(Guid runId);

    /// <summary>
    /// Resume a halted run, optionally steering it with <paramref name="note"/>
    /// as the next message to the same agent session. Re-runs the parked AI
    /// node. Requires the run to be <c>WaitingHuman</c> and <c>IsHalted</c> —
    /// distinct from <see cref="ResumeRunAsync"/>, which refuses WaitingHuman runs.
    /// </summary>
    Task ResumeFromHaltAsync(Guid runId, string? note);

    /// <summary>
    /// Park every run this process is driving for a host shutdown, then wait up
    /// to <paramref name="timeout"/> for their driving loops to unwind.
    ///
    /// A run on an AI node is halted the way <see cref="HaltRunAsync"/> halts
    /// one — <c>WaitingHuman</c> + <c>IsHalted</c>, keeping <c>CurrentNodeId</c>
    /// and the captured agent session — but stamped
    /// <c>HaltReason.Shutdown</c> and with no work-item transition, so the
    /// item stays Running on the server and the next start recognises the run as
    /// still ours and resumes it against the same session. A run on any other
    /// node is only cancelled and left <c>Running</c> for ordinary crash
    /// recovery: a Cmd or Condition node is cheap to redo and not worth a park
    /// a human might have to clear.
    ///
    /// The wait is the point rather than a courtesy: the park is written before
    /// the agent is killed, but the interrupted-node bookkeeping happens as the
    /// loop unwinds, so a process that exited first would leave exactly the
    /// half-written state this removes. Never throws — a drain that failed must
    /// not hold the process open.
    /// </summary>
    Task DrainForShutdownAsync(TimeSpan timeout);
    Task<LoopRunStatus?> GetRunStatusAsync(Guid runId);
    Task<IEnumerable<Guid>> GetActiveRunIdsAsync();

    /// <summary>
    /// Deliver the outcome of an externally-signalled node (PR webhook,
    /// human-feedback API, scheduled timer, etc.) and re-enter the run loop.
    /// </summary>
    Task SignalNodeResultAsync(Guid runId, Guid runNodeId, NodeSignal signal);

    Task ResumeRecoveredRunAsync(Guid runId);

    /// <summary>
    /// Re-enter the run at the template node corresponding to
    /// <paramref name="runNodeId"/>, replaying with the same
    /// <c>{{PreviousNode.Output}}</c> seed that the node saw the last
    /// time it started. Fails if the run is currently executing.
    /// </summary>
    Task RetryFromNodeAsync(Guid runId, Guid runNodeId);

    /// <summary>
    /// Execute the template's Cleanup node once (out-of-band), regardless of
    /// the current node, then leave the run state untouched. Used when a run
    /// is abandoned but external resources (worktrees, etc.) still need
    /// teardown. No-op if the template has no Cleanup node.
    /// </summary>
    Task CleanupRunAsync(Guid runId);
}

