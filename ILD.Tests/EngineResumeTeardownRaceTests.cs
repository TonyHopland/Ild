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
/// keeps using the harness's <b>single shared</b> in-memory <c>SqliteConnection</c> AFTER the
/// next park is observable: in <c>RunUntilParkAsync</c> the run status/reason are committed
/// (<c>UpdateRunAsync</c>) and only THEN does the drive read the DB again
/// (<c>GetEdgesForNodeIdsAsync</c>) and transition the work item. Tests key off the observable
/// state via a state-poll (<c>WaitUntilAsync</c>) and immediately dispose the harness — tearing
/// that connection down while the drive is still executing against it. Concurrent
/// operation-vs-Close on one <c>SqliteConnection</c> (which is not thread-safe) faults the
/// teardown.
///
/// This test makes the otherwise-rare interleaving reliable: a gate notifier holds a few real
/// operations in flight on the shared connection at the exact moment the flaky test would
/// dispose, so <c>Dispose()</c> reliably faults (pre-fix ~100% across the loop, locally). The
/// fix is to serialize teardown against the drive — <c>LoopEngineHarness.Dispose</c> should
/// drain the run's outstanding drive (wait for it to leave
/// <see cref="ILoopEngine.GetActiveRunIdsAsync"/> / await the drive task) before disposing
/// <c>Db</c>, so the connection is quiescent at teardown. (Signal-resume tests should likewise
/// wait for the run to go idle, not just for its parked state to be observable.) The exact
/// exception type varies by interleaving (NullReferenceException in CI, often a sibling
/// Sqlite/InvalidOperation/ObjectDisposed exception here) — all are the same defect surfacing as
/// a throw out of the harness teardown.
/// </summary>
public class EngineResumeTeardownRaceTests
{
    [Fact]
    public async Task Disposing_the_harness_right_after_a_signal_resume_parks_must_not_race_the_background_drive()
    {
        // A modest loop: each iteration independently races teardown against the in-flight
        // drive so a pre-fix failure is near-certain across the loop (reliably ~100% locally).
        // The fix is to serialize teardown against the drive — drain the run's outstanding drive
        // (wait for it to leave GetActiveRunIdsAsync) before disposing the shared connection.
        for (var iteration = 0; iteration < 80; iteration++)
        {
            var gate = new ReparkGate();
            var h = new LoopEngineHarness(gate);
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

            // Fire-and-forget resume: returns immediately; the drive runs on the thread pool
            // and, on re-parking, ends up back on the shared connection.
            await h.Engine.SignalNodeResultAsync(h.RunId, firstWaiting.Id,
                NodeSignal.Custom("Respond", "user-text"));

            // Wait until the signal-driven drive is provably back on the shared connection
            // right after the observable re-park — the exact window the flaky test disposes in.
            await gate.DriveBusyOnConnection.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The flaky test does exactly this: dispose immediately once the parked state is
            // observable. With the drive still on the shared connection this faults; a teardown
            // that drains the drive first (waits for the run to leave GetActiveRunIdsAsync /
            // awaits the drive task) lets the in-flight work finish and disposes cleanly.
            h.Dispose();
        }
    }

    /// <summary>
    /// Lets the initial park through, then on the signal-driven re-park launches a few real
    /// operations that stay in flight on the harness's shared <see cref="TestDb"/> connection —
    /// standing in, in a reliable/widened way, for the drive's own post-park DB use
    /// (<c>GetEdgesForNodeIdsAsync</c> + the work-item transition, which run after the observable
    /// status write). It signals once those are running so the test can dispose into the race.
    /// </summary>
    private sealed class ReparkGate : IRunNotifier
    {
        // Bounded window each in-flight op stays live on the shared connection. Long enough to
        // cover the test's dispose (a few ms), short enough that draining the drive (post-fix)
        // waits it out cheaply. Self-terminating, so a drain that awaits the drive never hangs.
        private static readonly TimeSpan InFlightWindow = TimeSpan.FromMilliseconds(60);

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
            // notifier call runs INSIDE the drive task (RunUntilParkAsync awaits it), so the
            // in-flight work below is part of the drive: draining the drive before teardown
            // waits for it, which is exactly what makes the fixed teardown safe.
            if (System.Threading.Interlocked.Increment(ref _waitingHumanTransitions) < 2)
                return;

            var db = Db;
            if (db is null) return;

            var conn = (Microsoft.Data.Sqlite.SqliteConnection)db.Context.Database.GetDbConnection();

            // Hold several real operations in flight on the shared connection for a bounded
            // window, then signal — standing in, reliably and in a widened way, for the drive's
            // own post-park DB use (GetEdgesForNodeIdsAsync + the work-item transition, which run
            // on this same connection after the observable status write). Awaited below so the
            // in-flight work is part of THIS drive task: a teardown that drains the drive waits it
            // out and disposes cleanly, while the unfixed teardown races it and faults in
            // SqliteConnection.Close() — the CI signature.
            const int inFlight = 3;
            var started = new System.Threading.CountdownEvent(inFlight);
            var ops = new Task[inFlight];
            for (var i = 0; i < inFlight; i++)
            {
                ops[i] = Task.Run(() =>
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        // Unbounded recursive CTE: the reader stays open, pulling rows off the
                        // shared connection for the window (or until it's torn down under us).
                        cmd.CommandText =
                            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT x FROM c";
                        using var reader = cmd.ExecuteReader();
                        started.Signal();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while (sw.Elapsed < InFlightWindow && reader.Read())
                        {
                            // keep the operation in flight across the teardown window
                        }
                    }
                    catch
                    {
                        // Connection torn down under the in-flight read — the real drive swallows
                        // this too (RunUntilParkAsync's catch). The fault we assert on surfaces on
                        // the disposing thread, out of TestDb.Dispose().
                        try { started.Signal(); } catch { /* already signalled */ }
                    }
                });
            }

            started.Wait(TimeSpan.FromSeconds(5));
            DriveBusyOnConnection.TrySetResult();
            await Task.WhenAll(ops);
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
