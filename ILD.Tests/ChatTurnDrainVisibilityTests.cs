using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A stopped turn is not over the moment it is cancelled: it still has to notice,
/// persist its partial reply and report itself finished. The chat is busy for all
/// of that, and a read that says otherwise takes the stop button off a turn that is
/// still running. These tests hold the runner to that, and to the rule the window
/// sits on: a chat's turn state is one value changed only by compare-and-swap, so a
/// reader never catches it mid-change, and moving a turn out of the running slot is
/// a claim exactly one thread can win — the one that then cancels and disposes it,
/// so no thread can cancel a source another has already disposed.
/// </summary>
public sealed class ChatTurnDrainVisibilityTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Records what the runner logs. A cancellation that raced a disposal is
    /// swallowed into <c>LogDebug</c> by the drain, so "nothing was logged" is how
    /// a test sees that the ownership rules held rather than merely survived.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ChatTurnRunner>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue($"{logLevel}: {formatter(state, exception)} | {exception?.GetType().Name}");
    }

    /// <summary>
    /// One turn, scripted: it blocks until cancelled, then stays in finalization —
    /// where a real turn persists its interrupted reply — until the test lets go.
    /// </summary>
    private sealed class BlockingTurn
    {
        public readonly TaskCompletionSource Running = new();
        public readonly TaskCompletionSource Finalizing = new();
        public readonly TaskCompletionSource Release = new();

        public async Task RunAsync(CancellationToken ct)
        {
            Running.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            Finalizing.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class Harness
    {
        public required ChatTurnRunner Runner { get; init; }
        public required RecordingLogger Log { get; init; }
        public required ConcurrentQueue<Guid> Started { get; init; }
        public required ConcurrentQueue<(Guid TurnId, bool Interrupted)> Completed { get; init; }
    }

    private static Harness NewRunner(
        Func<Guid, string, CancellationToken, Task> run,
        Action<Guid, Guid>? onCompleting = null,
        Func<Guid, Guid, Task>? onStarting = null)
    {
        var started = new ConcurrentQueue<Guid>();
        var completed = new ConcurrentQueue<(Guid, bool)>();

        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Guid turnId, string message, string? _, string? _, CancellationToken ct) => run(turnId, message, ct));

        var notifier = new Mock<IChatNotifier>();
        notifier.Setup(n => n.TurnStartedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(async (Guid chatSessionId, Guid turnId) =>
            {
                started.Enqueue(turnId);
                // The runner announces a start while the turn is only attached and
                // before it installs it, so a test that holds this open holds the
                // chat in exactly that state.
                if (onStarting is not null) await onStarting(chatSessionId, turnId);
            });
        notifier.Setup(n => n.TurnCompletedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()))
            .Returns((Guid chatSessionId, Guid turnId, bool interrupted) =>
            {
                // Runs where a client would hear the turn end, so a test can see what
                // a read arriving on that news would have been told.
                onCompleting?.Invoke(chatSessionId, turnId);
                completed.Enqueue((turnId, interrupted));
                return Task.CompletedTask;
            });

        var log = new RecordingLogger();
        var services = new ServiceCollection().AddScoped(_ => chat.Object).BuildServiceProvider();
        return new Harness
        {
            Runner = ActivatorUtilities.CreateInstance<ChatTurnRunner>(services, notifier.Object, log),
            Log = log,
            Started = started,
            Completed = completed,
        };
    }

    [Fact]
    public async Task A_chat_reads_as_busy_while_the_turn_a_stop_cancelled_is_still_finalizing()
    {
        var turn = new BlockingTurn();
        var h = NewRunner((_, _, ct) => turn.RunAsync(ct));
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience);
        await turn.Running.Task.WaitAsync(Patience);
        var live = h.Runner.ActiveTurnId(chatId);
        Assert.NotNull(live);

        var stop = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience);

        // Cancelled, and still the chat's turn: the interrupted reply is not written
        // yet and no completion has been announced.
        Assert.Equal(live, h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Completed);

        turn.Release.TrySetResult();
        await stop.WaitAsync(Patience);

        // And quiet again only once it has reported itself finished.
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        var (completedId, interrupted) = Assert.Single(h.Completed);
        Assert.Equal(live, completedId);
        Assert.True(interrupted, "a turn ended by a stop reports interrupted");
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_send_arriving_while_a_stop_is_draining_waits_for_it_and_starts_its_own_turn()
    {
        // The case the ownership rules have to survive: the send must not cancel or
        // dispose the turn the stop already owns, and must not start until that
        // turn has finished — the two share a transcript.
        var first = new BlockingTurn();
        var secondRan = new TaskCompletionSource();
        var firstHadFinished = false;
        var h = NewRunner(async (_, message, ct) =>
        {
            if (message == "one")
            {
                await first.RunAsync(ct);
                Volatile.Write(ref firstHadFinished, true);
                return;
            }
            secondRan.TrySetResult();
            await Task.CompletedTask;
        });
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience);
        await first.Running.Task.WaitAsync(Patience);
        var firstTurn = h.Runner.ActiveTurnId(chatId);

        var stop = h.Runner.InterruptAsync(chatId);
        await first.Finalizing.Task.WaitAsync(Patience);

        var send = h.Runner.SubmitAsync(chatId, "two");
        // The gate is held by the stop, so the send cannot have run yet — and the
        // chat still reads as running the turn being stopped.
        await Task.Delay(50);
        Assert.False(send.IsCompleted, "the send should wait for the stop it collided with");
        Assert.Equal(firstTurn, h.Runner.ActiveTurnId(chatId));

        first.Release.TrySetResult();
        await stop.WaitAsync(Patience);
        await send.WaitAsync(Patience);
        await secondRan.Task.WaitAsync(Patience);

        Assert.True(Volatile.Read(ref firstHadFinished), "the replacement ran before the stopped turn had finished");
        Assert.Equal(2, h.Started.Distinct().Count());
        Assert.Contains(h.Completed, c => c.TurnId == firstTurn && c.Interrupted);
        await WaitUntilAsync(() => h.Completed.Count == 2, "both turns should report themselves finished");
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_delete_arriving_while_a_stop_is_draining_runs_after_it()
    {
        var turn = new BlockingTurn();
        var h = NewRunner((_, _, ct) => turn.RunAsync(ct));
        var chatId = Guid.NewGuid();
        var deleted = false;

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience);
        await turn.Running.Task.WaitAsync(Patience);

        var stop = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience);

        var delete = h.Runner.DeleteAsync(chatId, () =>
        {
            deleted = true;
            return Task.CompletedTask;
        });
        await Task.Delay(50);
        Assert.False(delete.IsCompleted, "the delete should wait for the stop in progress");
        Assert.False(deleted, "the chat should not be deleted under a turn that is still finalizing");

        turn.Release.TrySetResult();
        await stop.WaitAsync(Patience);
        await delete.WaitAsync(Patience);

        Assert.True(deleted);
        Assert.Single(h.Completed);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_second_stop_during_a_drain_cancels_nothing_twice_and_adds_no_second_completion()
    {
        var turn = new BlockingTurn();
        var h = NewRunner((_, _, ct) => turn.RunAsync(ct));
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience);
        await turn.Running.Task.WaitAsync(Patience);

        var first = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience);
        var second = h.Runner.InterruptAsync(chatId);

        turn.Release.TrySetResult();
        await first.WaitAsync(Patience);
        await second.WaitAsync(Patience);

        Assert.Single(h.Completed);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_stop_racing_a_turn_that_ends_on_its_own_neither_throws_nor_reports_twice()
    {
        // Whoever takes the turn out of the live map owns cancelling and disposing
        // it, and exactly one side can. Run the two into each other repeatedly: a
        // cancel against a disposed source would be swallowed into the log, so an
        // empty log is the assertion that it never happened.
        const int rounds = 300;
        var h = NewRunner((_, _, _) => Task.CompletedTask);

        for (var i = 0; i < rounds; i++)
        {
            var chatId = Guid.NewGuid();
            await h.Runner.SubmitAsync(chatId, $"message {i}").WaitAsync(Patience);
            await h.Runner.InterruptAsync(chatId).WaitAsync(Patience);
            Assert.Null(h.Runner.ActiveTurnId(chatId));
        }

        await WaitUntilAsync(
            () => h.Completed.Count == rounds, $"every one of the {rounds} turns should report itself finished");
        Assert.Equal(rounds, h.Started.Distinct().Count());
        Assert.Equal(h.Started.OrderBy(id => id), h.Completed.Select(c => c.TurnId).OrderBy(id => id));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_stopped_turn_is_no_longer_the_chats_turn_by_the_time_it_says_it_has_finished()
    {
        // The completion is the last thing a client hears about this turn. A reader
        // arriving on that news must not still be handed its id, because no further
        // completion is coming to take the stop button down again.
        ChatTurnRunner? runner = null;
        Guid? reportedWhileCompleting = null;
        var turn = new BlockingTurn();
        var h = NewRunner(
            (_, _, ct) => turn.RunAsync(ct),
            onCompleting: (chatId, _) => reportedWhileCompleting = runner!.ActiveTurnId(chatId));
        runner = h.Runner;
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience);
        await turn.Running.Task.WaitAsync(Patience);
        var stop = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience);
        turn.Release.TrySetResult();
        await stop.WaitAsync(Patience);

        Assert.Single(h.Completed);
        Assert.Null(reportedWhileCompleting);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_chat_never_reads_as_idle_as_its_turn_moves_from_running_to_finalizing()
    {
        // The hand-off from running to finishing, read from another thread throughout.
        // It is one swap of one value, so a reader watching from before the stop
        // until finalization has begun must never be told the chat is idle. The
        // reader is stopped at that point on purpose: once the turn finishes it lets
        // go of the state, and reading idle then is the right answer rather than the
        // gap under test.
        const int rounds = 200;
        var turns = new ConcurrentDictionary<string, BlockingTurn>();
        var h = NewRunner((_, message, ct) => turns.GetOrAdd(message, _ => new BlockingTurn()).RunAsync(ct));

        for (var i = 0; i < rounds; i++)
        {
            var chatId = Guid.NewGuid();
            var message = $"message {i}";
            // Registered before the send, because the turn body starts on its own
            // thread and can reach the map before or after SubmitAsync returns.
            var turn = turns.GetOrAdd(message, _ => new BlockingTurn());
            await h.Runner.SubmitAsync(chatId, message).WaitAsync(Patience);
            await turn.Running.Task.WaitAsync(Patience);

            var reading = true;
            var sawIdle = false;
            var reader = Task.Run(() =>
            {
                while (Volatile.Read(ref reading))
                    if (h.Runner.ActiveTurnId(chatId) is null) Volatile.Write(ref sawIdle, true);
            });

            var stop = h.Runner.InterruptAsync(chatId);
            await turn.Finalizing.Task.WaitAsync(Patience);
            Volatile.Write(ref reading, false);
            await reader.WaitAsync(Patience);

            turn.Release.TrySetResult();
            await stop.WaitAsync(Patience);

            Assert.False(
                Volatile.Read(ref sawIdle),
                $"round {i}: the chat read as idle while its turn was moving from running to finalizing");
        }

        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_reader_sees_the_chat_busy_from_the_moment_a_turn_starts_until_it_has_finished()
    {
        // The whole liveness contract, watched from another thread while turns are
        // started, retire themselves and are stopped on top of each other:
        //
        //   - the chat may not read as idle once a reader has seen a turn, until
        //     that turn has actually stopped executing, and
        //   - a reader may never be handed a turn whose completion has already been
        //     announced, because no further completion is coming for it.
        //
        // Both are properties of the whole turn state being one value: a reader gets
        // what it was before a change or what it is after, never a step inside one.
        const int rounds = 4000;
        var finishedExecuting = new ConcurrentDictionary<Guid, bool>();
        // One turn per round, so a queue of what has been announced is enough and
        // gives the reader a properly ordered read of it.
        var announced = new ConcurrentQueue<Guid>();
        var jitter = new Random(20260921);

        var h = NewRunner(
            async (turnId, _, ct) =>
            {
                int micros;
                lock (jitter) micros = jitter.Next(0, 30);
                if (micros > 0)
                {
                    try { await Task.Delay(TimeSpan.FromMicroseconds(micros), ct); }
                    catch (OperationCanceledException) { }
                }
                finishedExecuting[turnId] = true;
            },
            onCompleting: (_, turnId) => announced.Enqueue(turnId));

        for (var i = 0; i < rounds; i++)
        {
            var chatId = Guid.NewGuid();
            announced.Clear();
            string? violation = null;
            var reading = true;
            var reader = Task.Run(() =>
            {
                Guid? seen = null;
                while (Volatile.Read(ref reading))
                {
                    // What has already been announced is read first, so a completion
                    // landing between the two reads is not mistaken for the state
                    // being wrong: only a turn announced before we asked counts.
                    var alreadyAnnounced = announced.TryPeek(out var done) ? done : (Guid?)null;
                    var now = h.Runner.ActiveTurnId(chatId);
                    if (now is null)
                    {
                        // The body records itself finished before the turn lets go of
                        // the state, so an idle read always has that record already.
                        if (seen is Guid last && !finishedExecuting.ContainsKey(last))
                            violation ??= $"read idle while turn {last} was still running";
                    }
                    else
                    {
                        if (now == alreadyAnnounced)
                            violation ??= $"read turn {now} after its completion was announced";
                        seen = now;
                    }
                }
            });

            await h.Runner.SubmitAsync(chatId, $"message {i}").WaitAsync(Patience);
            // Races the turn's own retirement: sometimes it claims a live turn,
            // sometimes it finds one that has just gone.
            await h.Runner.InterruptAsync(chatId).WaitAsync(Patience);
            Volatile.Write(ref reading, false);
            await reader.WaitAsync(Patience);

            Assert.True(violation is null, $"round {i}: {violation}");
            Assert.Null(h.Runner.ActiveTurnId(chatId));
        }

        Assert.Equal(rounds, h.Started.Count);
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_send_landing_as_the_turn_it_replaces_finishes_keeps_the_chat_busy()
    {
        // The other half of the hand-over: a send arriving just as the turn it is
        // replacing ends on its own. Sometimes it displaces a live turn, sometimes it
        // finds one that let go between attaching and installing — and that second
        // branch must wait for the turn without cancelling or disposing a source its
        // owner has already disposed. A reader watches throughout, holding the same
        // two rules. Which branch each round takes is left to the race here; the test
        // below scripts the rarer one so it is covered on every run.
        const int rounds = 4000;
        var finishedExecuting = new ConcurrentDictionary<Guid, bool>();
        var announced = new ConcurrentQueue<Guid>();
        var jitter = new Random(20260922);

        var h = NewRunner(
            async (turnId, _, ct) =>
            {
                int micros;
                lock (jitter) micros = jitter.Next(0, 30);
                if (micros > 0)
                {
                    try { await Task.Delay(TimeSpan.FromMicroseconds(micros), ct); }
                    catch (OperationCanceledException) { }
                }
                finishedExecuting[turnId] = true;
            },
            onCompleting: (_, turnId) => announced.Enqueue(turnId));

        for (var i = 0; i < rounds; i++)
        {
            var chatId = Guid.NewGuid();
            announced.Clear();
            string? violation = null;
            var reading = true;
            var reader = Task.Run(() =>
            {
                Guid? seen = null;
                while (Volatile.Read(ref reading))
                {
                    var alreadyAnnounced = announced.TryPeek(out var done) ? done : (Guid?)null;
                    var now = h.Runner.ActiveTurnId(chatId);
                    if (now is null)
                    {
                        if (seen is Guid last && !finishedExecuting.ContainsKey(last))
                            violation ??= $"read idle while turn {last} was still running";
                    }
                    else
                    {
                        if (now == alreadyAnnounced)
                            violation ??= $"read turn {now} after its completion was announced";
                        seen = now;
                    }
                }
            });

            // The second send races the first turn's own retirement.
            await h.Runner.SubmitAsync(chatId, $"first {i}").WaitAsync(Patience);
            await h.Runner.SubmitAsync(chatId, $"second {i}").WaitAsync(Patience);
            await h.Runner.InterruptAsync(chatId).WaitAsync(Patience);
            Volatile.Write(ref reading, false);
            await reader.WaitAsync(Patience);

            Assert.True(violation is null, $"round {i}: {violation}");
            Assert.Null(h.Runner.ActiveTurnId(chatId));
        }

        // Two turns a round, each announced once and reported finished once.
        await WaitUntilAsync(
            () => h.Completed.Count == rounds * 2, "every turn should report itself finished");
        Assert.Equal(rounds * 2, h.Started.Count);
        Assert.Equal(h.Started.OrderBy(id => id), h.Completed.Select(c => c.TurnId).OrderBy(id => id));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_send_that_installs_over_a_turn_which_has_just_retired_waits_for_it_uncancelled()
    {
        // The same hand-over as above, but scripted rather than raced, so it is
        // reached on every run: the first turn is let go, and finishes and retires
        // itself, while the second send sits in its own start announcement — after
        // attaching, before installing. The send then installs over a chat whose live
        // turn has gone, and must wait for it without cancelling or disposing a source
        // its owner has already disposed.
        var chatId = Guid.NewGuid();
        var firstRunning = new TaskCompletionSource();
        var firstMayFinish = new TaskCompletionSource();
        var firstRetired = new TaskCompletionSource();
        var second = new BlockingTurn();
        var firstTurnId = Guid.Empty;
        var starts = 0;

        var h = NewRunner(
            async (_, message, ct) =>
            {
                if (message != "first")
                {
                    await second.RunAsync(ct);
                    return;
                }

                firstRunning.TrySetResult();
                // Ends of its own accord: nothing cancels this turn.
                await firstMayFinish.Task;
            },
            onCompleting: (_, turnId) =>
            {
                // Announced after the turn has retired, so this is the point from
                // which the second send is certain to find no live turn to displace.
                if (turnId == firstTurnId) firstRetired.TrySetResult();
            },
            onStarting: async (_, turnId) =>
            {
                if (Interlocked.Increment(ref starts) != 2) return;
                Assert.NotEqual(firstTurnId, turnId);
                firstMayFinish.TrySetResult();
                await firstRetired.Task.WaitAsync(Patience);
            });

        await h.Runner.SubmitAsync(chatId, "first").WaitAsync(Patience);
        await firstRunning.Task.WaitAsync(Patience);
        firstTurnId = Assert.IsType<Guid>(h.Runner.ActiveTurnId(chatId));

        string? violation = null;
        var reading = true;
        var reader = Task.Run(() =>
        {
            while (Volatile.Read(ref reading))
            {
                var alreadyAnnounced = h.Completed.TryPeek(out var done) ? done.TurnId : (Guid?)null;
                var now = h.Runner.ActiveTurnId(chatId);
                if (now is null)
                    violation ??= "read idle while a turn was in hand throughout";
                else if (now == alreadyAnnounced)
                    violation ??= $"read turn {now} after its completion was announced";
            }
        });

        await h.Runner.SubmitAsync(chatId, "second").WaitAsync(Patience);
        await second.Running.Task.WaitAsync(Patience);
        Volatile.Write(ref reading, false);
        await reader.WaitAsync(Patience);
        Assert.True(violation is null, violation);

        // Exactly the branch this test exists for, so it cannot quietly stop covering
        // it: one send waited for a turn it did not own.
        Assert.Equal(1, h.Runner.TurnsLeftToRetireCount);

        // And it neither cancelled nor disposed that turn's source: either would have
        // thrown ObjectDisposedException inside the drain, which is swallowed but
        // logged, so an empty log is what says it did not happen.
        Assert.Empty(h.Log.Entries);

        var secondTurnId = Assert.IsType<Guid>(h.Runner.ActiveTurnId(chatId));
        Assert.NotEqual(firstTurnId, secondTurnId);
        Assert.Equal(new[] { firstTurnId, secondTurnId }, h.Started);
        Assert.Equal(new[] { firstTurnId }, h.Completed.Select(c => c.TurnId));

        // The turn that ended on its own reported itself finished, not interrupted.
        Assert.Equal(new[] { false }, h.Completed.Select(c => c.Interrupted));

        second.Release.TrySetResult();
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Equal(new[] { firstTurnId, secondTurnId }, h.Completed.Select(c => c.TurnId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_send_whose_start_cannot_be_announced_leaves_the_chat_as_it_found_it()
    {
        // A send holds the chat as busy from before it announces itself, so if that
        // announcement fails the send has to let go again: a chat left reading busy
        // for a turn that is never going to run is this bug over again, and nothing
        // else would ever clear it.
        var chatId = Guid.NewGuid();
        var turn = new BlockingTurn();
        var failStart = true;

        var h = NewRunner(
            (_, _, ct) => turn.RunAsync(ct),
            onStarting: (_, _) => failStart
                ? Task.FromException(new InvalidOperationException("no one to tell"))
                : Task.CompletedTask);

        // Into an idle chat: it stays idle, with nothing left behind to clear.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.SubmitAsync(chatId, "one"));
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Equal(0, h.Runner.ActiveTurnCount);
        Assert.False(turn.Running.Task.IsCompleted);

        failStart = false;
        await h.Runner.SubmitAsync(chatId, "two").WaitAsync(Patience);
        await turn.Running.Task.WaitAsync(Patience);
        var live = Assert.IsType<Guid>(h.Runner.ActiveTurnId(chatId));

        // Into a busy chat: the turn it would have replaced is neither cancelled nor
        // displaced, so the chat still reads as busy with that turn.
        failStart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.SubmitAsync(chatId, "three"));
        Assert.Equal(live, h.Runner.ActiveTurnId(chatId));
        Assert.False(turn.Finalizing.Task.IsCompleted);

        turn.Release.TrySetResult();
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Equal(0, h.Runner.ActiveTurnCount);
        Assert.Equal(0, h.Runner.GateCount);

        // One turn ever ran, and it is the one that was announced.
        Assert.Equal(new[] { live }, h.Completed.Select(c => c.TurnId));
        Assert.Contains(live, h.Started);
        Assert.Empty(h.Log.Entries);
    }

    // Polls a background step that nothing can be awaited on, with a deadline far
    // beyond what it needs — a failure means it never happened, not that it was slow.
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.Add(Patience);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition(), because);
    }
}
