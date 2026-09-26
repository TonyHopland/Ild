using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// Regression for the CI flake in
/// <c>EngineSignalValidationTests.SignalNodeResultAsync_resume_then_repark_sets_fresh_reason</c>,
/// which failed intermittently with a <see cref="System.NullReferenceException"/> thrown from
/// <c>SqliteConnection.Close()</c> inside <c>LoopEngineHarness.Dispose()</c> /
/// <c>TestDb.Dispose()</c> — never in an assertion. It reproduced roughly once per few hundred
/// suite runs, only under load (a 2-core CI runner), which is why it never repro'd on a
/// many-core dev box.
///
/// Root cause: <see cref="ILoopEngine.SignalNodeResultAsync"/> persists the run's parked state
/// and then resumes the run on a <b>fire-and-forget</b> background task
/// (<c>LoopEngine.LaunchAfterAwaitAsync</c> → <c>Task.Run(RunUntilParkAsync)</c>). That drive
/// keeps issuing commands on the harness's <b>single shared</b> in-memory <c>SqliteConnection</c>
/// AFTER the next park is observable: in <c>RunUntilParkAsync</c> the run status/reason are
/// committed (<c>UpdateRunAsync</c>) and only THEN does the drive read the DB again
/// (<c>GetEdgesForNodeIdsAsync</c>) and transition the work item. The signal-resume tests keyed
/// off that observable state with a state-poll (<c>WaitUntilAsync(ReloadRun)</c>) — issuing their
/// OWN commands on the same shared connection, from the test thread, concurrently with the drive.
/// A <c>SqliteConnection</c> is not thread-safe: two threads mutating its internal command list at
/// once intermittently corrupt it, and a later <c>Close()</c> then dereferences the corrupted
/// state and throws (the CI signature; a sibling Sqlite/InvalidOperation/ObjectDisposed exception
/// under heavier amplification — all the same defect).
///
/// Fix: the signal-resume tests must let the run go fully idle before touching the shared
/// connection — <see cref="LoopEngineHarness.WaitUntilIdleAsync"/> awaits the outstanding drive
/// task WITHOUT issuing any command, so only the drive uses the connection while it is live. Its
/// teardown (<c>LoopEngineHarness.Dispose</c>) drains the same way, so the connection is quiescent
/// and single-threaded at <c>Close()</c>. This test holds the drive in its post-park window (a
/// stand-in for its real <c>GetEdgesForNodeIdsAsync</c> + work-item transition) and checks that
/// the drain is still waiting on it: a drain that returned early (e.g. a naive
/// <c>GetActiveRunIdsAsync</c> poll that trips over the fire-and-forget launch gap) would let the
/// read and dispose race the still-live drive, exactly as the original did.
/// </summary>
public class EngineResumeTeardownRaceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Signal_resume_drive_is_drained_before_the_shared_connection_is_read_or_disposed()
    {
        var gate = new ReparkGate();
        using var h = new LoopEngineHarness(gate);
        gate.Db = h.Db;

        h.AddNode("h1", NodeType.Human);
        h.AddNode("h2", NodeType.Human);
        h.AddEdge("h1", "h2", EdgeType.Custom, "Respond");

        var humanExec = new ScriptedExecutor(NodeType.Human,
            new NodeOutcome.NodeStarting("ask-1"),
            new NodeOutcome.WaitingAction("First question", "prompt"));
        humanExec.Then(
            new NodeOutcome.NodeStarting("re-entry"),
            new NodeOutcome.Success(EdgeType.Custom, "answered", "Respond"));
        humanExec.Then(
            new NodeOutcome.NodeStarting("ask-2"),
            new NodeOutcome.WaitingAction("Second question", "prompt"));
        h.Registry.Register(humanExec);

        h.SeedRun("h1");
        await h.RunAsync(); // parks at h1 (first WaitingHuman transition passes the gate)

        var firstWaiting = h.ReloadRunNodes().Single(rn => rn.Status == LoopRunNodeStatus.WaitingHuman);

        try
        {
            // Fire-and-forget resume: returns immediately; the drive runs on the thread pool and,
            // on re-parking, is held by the gate after using the shared connection.
            await h.Engine.SignalNodeResultAsync(h.RunId, firstWaiting.Id,
                NodeSignal.Custom("Respond", "user-text"));
            await gate.DriveBusyOnConnection.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

            // The re-park is observable and the drive is still live: the drain must not be done.
            var idle = h.WaitUntilIdleAsync();
            await h.Engine.GetActiveRunIdsAsync();
            Assert.False(idle.IsCompleted, "the drain finished while the resume drive was still live");

            gate.Release.TrySetResult();
            await idle.WaitAsync(Patience, TestContext.Current.CancellationToken);
        }
        finally
        {
            // Released on every path, so the harness's own drain at dispose cannot hang.
            gate.Release.TrySetResult();
        }

        // Provably drained: the drive has released the connection and left the active set, so
        // the read below and the dispose at end-of-scope are single-threaded on the connection.
        Assert.DoesNotContain(h.RunId, await h.Engine.GetActiveRunIdsAsync());

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
        Assert.Equal("Second question", run.HumanFeedbackReason);
    }

    /// <summary>
    /// Lets the initial inline park through, then on the signal-driven re-park uses the harness's
    /// shared <see cref="TestDb"/> connection once and holds the drive until the test releases it
    /// — a stand-in for the drive's own post-park DB use (<c>GetEdgesForNodeIdsAsync</c> + the
    /// work-item transition, which run on this connection AFTER the observable status write). It
    /// runs INLINE on the drive task (<c>RunUntilParkAsync</c> awaits this notifier), so the hold
    /// is part of the drive: draining the drive waits for it.
    /// </summary>
    private sealed class ReparkGate : IRunNotifier
    {
        public TestDb? Db;
        public readonly TaskCompletionSource DriveBusyOnConnection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waitingHumanTransitions;

        public async Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
        {
            if (newStatus != LoopRunStatus.WaitingHuman)
                return;

            // 1st WaitingHuman = the initial park driven inline by RunAsync — let it pass or
            // RunAsync would deadlock. 2nd = the signal-driven re-park we want to catch.
            if (System.Threading.Interlocked.Increment(ref _waitingHumanTransitions) < 2)
                return;

            var db = Db;
            if (db is null) return;

            // A real query on the shared connection from the drive, as its own post-park DB use
            // makes. Single-threaded (this is the drive): the defect the fix addresses is a SECOND
            // thread (the old ReloadRun poll, or Close) touching the connection concurrently.
            _ = db.Fresh().LoopRuns.AsNoTracking().Count();
            DriveBusyOnConnection.TrySetResult();
            await Release.Task;
        }

        public Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus) => Task.CompletedTask;
        public Task EventLoggedAsync(Guid runId, string message, string eventType, Guid? nodeId, Guid? runNodeId) => Task.CompletedTask;
        public Task PausedAsync(Guid runId) => Task.CompletedTask;
        public Task ResumedAsync(Guid runId) => Task.CompletedTask;
        public Task HaltedAsync(Guid runId) => Task.CompletedTask;
        public Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq) => Task.CompletedTask;
        public Task PrSnapshotChangedAsync(Guid runId) => Task.CompletedTask;
        public Task PrQueueChangedAsync(Guid runId) => Task.CompletedTask;
    }
}
