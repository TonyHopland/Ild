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
/// and single-threaded at <c>Close()</c>. This test widens the drive's post-park window (a stand-in
/// for its real <c>GetEdgesForNodeIdsAsync</c> + work-item transition), then relies on that drain:
/// if the drain returned early (e.g. a naive <c>GetActiveRunIdsAsync</c> poll that trips over the
/// fire-and-forget launch gap) the subsequent read and dispose would race the still-live drive and
/// this loop would fault, exactly as the original did.
/// </summary>
public class EngineResumeTeardownRaceTests
{
    [Fact]
    public async Task Signal_resume_drive_is_drained_before_the_shared_connection_is_read_or_disposed()
    {
        // A modest loop: each iteration re-parks via the fire-and-forget resume drive while the
        // gate holds that drive on the shared connection, then drains it before reading/disposing.
        // Pre-fix (poll the connection while the drive is live) this faulted across the loop.
        for (var iteration = 0; iteration < 60; iteration++)
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

            // Fire-and-forget resume: returns immediately; the drive runs on the thread pool and,
            // on re-parking, ends up back on the shared connection (the gate widens that window).
            await h.Engine.SignalNodeResultAsync(h.RunId, firstWaiting.Id,
                NodeSignal.Custom("Respond", "user-text"));

            // Let the drive get provably busy on the shared connection right after the observable
            // re-park — the exact window the flaky test used to poll (and dispose) into.
            await gate.DriveBusyOnConnection.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The fix: drain the drive before touching the connection. WaitUntilIdleAsync awaits
            // the drive task and issues no command, so the test thread never races the drive.
            await h.WaitUntilIdleAsync();

            // Provably drained: the drive has released the connection and left the active set, so
            // the read below and the dispose at end-of-scope are single-threaded on the connection.
            Assert.DoesNotContain(h.RunId, await h.Engine.GetActiveRunIdsAsync());

            var run = h.ReloadRun();
            Assert.Equal(LoopRunStatus.WaitingHuman, run.Status);
            Assert.Equal("Second question", run.HumanFeedbackReason);
        }
    }

    /// <summary>
    /// Lets the initial inline park through, then on the signal-driven re-park keeps the drive
    /// genuinely busy on the harness's shared <see cref="TestDb"/> connection for a bounded window
    /// — a stand-in for the drive's own post-park DB use (<c>GetEdgesForNodeIdsAsync</c> + the
    /// work-item transition, which run on this connection AFTER the observable status write). It
    /// runs INLINE on the drive task (<c>RunUntilParkAsync</c> awaits this notifier), so the busy
    /// work is part of the drive: draining the drive waits it out. It signals once the busy loop is
    /// underway so the test can proceed into the (now drained) window.
    /// </summary>
    private sealed class ReparkGate : IRunNotifier
    {
        // Bounded window the drive stays busy on the shared connection. Long enough to give the
        // teardown/idle-wait something real to serialize against, short enough that draining the
        // drive waits it out cheaply. Self-terminating, so a drain that awaits the drive never hangs.
        private static readonly TimeSpan BusyWindow = TimeSpan.FromMilliseconds(60);

        public TestDb? Db;
        public readonly TaskCompletionSource DriveBusyOnConnection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waitingHumanTransitions;

        public async Task RunStateChangedAsync(Guid runId, LoopRunStatus oldStatus, LoopRunStatus newStatus)
        {
            if (newStatus != LoopRunStatus.WaitingHuman)
                return;

            // 1st WaitingHuman = the initial park driven inline by RunAsync — let it pass or
            // RunAsync would deadlock. 2nd = the signal-driven re-park we want to catch. This
            // notifier call runs INSIDE the drive task (RunUntilParkAsync awaits it), so the busy
            // work below is part of the drive: draining the drive before the test reads or disposes
            // waits for it, which is exactly what makes the fix safe.
            if (System.Threading.Interlocked.Increment(ref _waitingHumanTransitions) < 2)
                return;

            var db = Db;
            if (db is null) return;

            // Keep the drive issuing real queries on the shared connection for the window, the same
            // way its own post-park DB use does. Single-threaded (this is the drive): the defect the
            // fix addresses is a SECOND thread (the old ReloadRun poll, or Close) touching the
            // connection concurrently — which the drain now prevents by waiting this out first.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < BusyWindow)
            {
                try
                {
                    // A fresh context on the same shared connection — mirrors the run-state reads
                    // the drive and the flaky test both make against it.
                    _ = db.Fresh().LoopRuns.AsNoTracking().Count();
                }
                catch
                {
                    // Connection torn down under us — the real drive swallows this too
                    // (RunUntilParkAsync's catch). A correctly-drained teardown never does this.
                    break;
                }
                DriveBusyOnConnection.TrySetResult();
                await Task.Yield();
            }
            DriveBusyOnConnection.TrySetResult();
        }

        public Task NodeStateChangedAsync(Guid runId, Guid nodeId, LoopRunNodeStatus oldStatus, LoopRunNodeStatus newStatus) => Task.CompletedTask;
        public Task EventLoggedAsync(Guid runId, string message, string eventType, Guid? nodeId, Guid? runNodeId) => Task.CompletedTask;
        public Task PausedAsync(Guid runId) => Task.CompletedTask;
        public Task ResumedAsync(Guid runId) => Task.CompletedTask;
        public Task HaltedAsync(Guid runId) => Task.CompletedTask;
        public Task NodeProgressAsync(Guid runId, Guid nodeId, string line, long seq) => Task.CompletedTask;
        public Task PrSnapshotChangedAsync(Guid runId) => Task.CompletedTask;
    }
}
