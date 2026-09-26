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
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = new();

        public required ChatTurnRunner Runner { get; init; }
        public required RecordingLogger Log { get; init; }
        public ConcurrentQueue<Guid> Started { get; } = new();
        public ConcurrentQueue<(Guid TurnId, bool Interrupted)> Completed { get; } = new();

        public void RecordCompleted(Guid turnId, bool interrupted)
        {
            lock (_gate)
            {
                Completed.Enqueue((turnId, interrupted));
                foreach (var waiter in _waiters.Where(w => w.Count <= Completed.Count).ToList())
                {
                    waiter.Tcs.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> turns have reported finished.</summary>
        public Task CompletedAtLeast(int count)
        {
            lock (_gate)
            {
                if (Completed.Count >= count) return Task.CompletedTask;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, tcs));
                return tcs.Task;
            }
        }
    }

    // Disposed with the turn's DI scope, which the runner closes once the service
    // has finished with the turn — after the reply is persisted and before the turn
    // retires and is announced finished.
    private sealed class RunsWhenTheTurnsScopeCloses(Action? onDispose) : IDisposable
    {
        public void Dispose() => onDispose?.Invoke();
    }

    private static Harness NewRunner(
        Func<Guid, string, CancellationToken, Task> run,
        Action<Guid, Guid>? onCompleting = null,
        Func<Guid, Guid, Task>? onStarting = null,
        Action? onScopeClose = null)
    {
        Harness? harness = null;

        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Guid turnId, string message, string? _, string? _, CancellationToken ct) => run(turnId, message, ct));

        var notifier = new Mock<IChatNotifier>();
        notifier.Setup(n => n.TurnStartedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(async (Guid chatSessionId, Guid turnId) =>
            {
                harness!.Started.Enqueue(turnId);
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
                harness!.RecordCompleted(turnId, interrupted);
                return Task.CompletedTask;
            });

        var log = new RecordingLogger();
        var services = new ServiceCollection()
            .AddScoped(_ => new RunsWhenTheTurnsScopeCloses(onScopeClose))
            // Resolved with the service so the scope owns the hook and closes it at
            // the same point, without the runner knowing anything about it.
            .AddScoped(sp => { sp.GetRequiredService<RunsWhenTheTurnsScopeCloses>(); return chat.Object; })
            .BuildServiceProvider();
        harness = new Harness
        {
            Runner = ActivatorUtilities.CreateInstance<ChatTurnRunner>(services, notifier.Object, log),
            Log = log,
        };
        return harness;
    }

    [Fact]
    public async Task A_chat_reads_as_busy_while_the_turn_a_stop_cancelled_is_still_finalizing()
    {
        var turn = new BlockingTurn();
        var h = NewRunner((_, _, ct) => turn.RunAsync(ct));
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await turn.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var live = h.Runner.ActiveTurnId(chatId);
        Assert.NotNull(live);

        var stop = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Cancelled, and still the chat's turn: the interrupted reply is not written
        // yet and no completion has been announced.
        Assert.Equal(live, h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Completed);

        turn.Release.TrySetResult();
        await stop.WaitAsync(Patience, TestContext.Current.CancellationToken);

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

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await first.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var firstTurn = h.Runner.ActiveTurnId(chatId);

        var stop = h.Runner.InterruptAsync(chatId);
        await first.Finalizing.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var send = h.Runner.SubmitAsync(chatId, "two");
        // The gate is held by the stop, so the send cannot have run yet — and the
        // chat still reads as running the turn being stopped. Nothing past the gate
        // awaits anything incomplete, so a send that got through would already be done.
        Assert.False(send.IsCompleted, "the send should wait for the stop it collided with");
        Assert.Equal(firstTurn, h.Runner.ActiveTurnId(chatId));

        first.Release.TrySetResult();
        await stop.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await send.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await secondRan.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(Volatile.Read(ref firstHadFinished), "the replacement ran before the stopped turn had finished");
        Assert.Equal(2, h.Started.Distinct().Count());
        Assert.Contains(h.Completed, c => c.TurnId == firstTurn && c.Interrupted);
        await h.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Completed.Count);
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_delete_arriving_while_a_stop_is_draining_runs_after_it()
    {
        var turn = new BlockingTurn();
        var h = NewRunner((_, _, ct) => turn.RunAsync(ct));
        var chatId = Guid.NewGuid();
        var deleted = false;

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await turn.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var stop = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var delete = h.Runner.DeleteAsync(chatId, () =>
        {
            deleted = true;
            return Task.CompletedTask;
        });
        Assert.False(delete.IsCompleted, "the delete should wait for the stop in progress");
        Assert.False(deleted, "the chat should not be deleted under a turn that is still finalizing");

        turn.Release.TrySetResult();
        await stop.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await delete.WaitAsync(Patience, TestContext.Current.CancellationToken);

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

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await turn.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var first = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var second = h.Runner.InterruptAsync(chatId);

        turn.Release.TrySetResult();
        await first.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await second.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Single(h.Completed);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_stop_racing_a_turn_that_ends_on_its_own_neither_throws_nor_reports_twice()
    {
        // Whoever takes the turn out of the live map owns cancelling and disposing
        // it, and exactly one side can. Both orders are scripted: a stop that lands
        // while the finished turn is closing its scope, still live, and a stop that
        // arrives once the turn has retired itself. A cancel against a disposed
        // source would be swallowed into the log, so an empty log is the assertion
        // that it never happened.
        const int rounds = 2;
        ChatTurnRunner? runner = null;
        var tokens = new ConcurrentDictionary<string, CancellationToken>();
        var readsAtCompletion = new ConcurrentQueue<Guid?>();
        Guid? stopWhileClosing = null;
        Task? stopInTheGap = null;

        var h = NewRunner(
            (_, message, ct) =>
            {
                tokens[message] = ct;
                return Task.CompletedTask;
            },
            onCompleting: (chatId, _) => readsAtCompletion.Enqueue(runner!.ActiveTurnId(chatId)),
            onScopeClose: () =>
            {
                if (stopWhileClosing is not Guid chatId) return;
                // Not awaited: the stop cancels before it waits for this very turn, so
                // waiting for the token waits for the part that matters.
                stopInTheGap = runner!.InterruptAsync(chatId);
                tokens["stopped while closing"].WaitHandle.WaitOne(Patience);
            });
        runner = h.Runner;

        var closing = Guid.NewGuid();
        stopWhileClosing = closing;
        await h.Runner.SubmitAsync(closing, "stopped while closing").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);
        await stopInTheGap!.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(tokens["stopped while closing"].IsCancellationRequested, "the stop should have landed while the turn was still live");
        Assert.Null(h.Runner.ActiveTurnId(closing));

        stopWhileClosing = null;
        var retired = Guid.NewGuid();
        await h.Runner.SubmitAsync(retired, "ended first").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.Runner.InterruptAsync(retired).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(h.Runner.ActiveTurnId(retired));

        Assert.Equal(rounds, h.Completed.Count);
        Assert.Equal(rounds, h.Started.Distinct().Count());
        Assert.Equal(h.Started.OrderBy(id => id), h.Completed.Select(c => c.TurnId).OrderBy(id => id));
        // A turn has let go of the chat by the time it says it has finished.
        Assert.All(readsAtCompletion, read => Assert.Null(read));
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

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await turn.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var stop = h.Runner.InterruptAsync(chatId);
        await turn.Finalizing.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        turn.Release.TrySetResult();
        await stop.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Single(h.Completed);
        Assert.Null(reportedWhileCompleting);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_send_that_installs_over_a_turn_which_has_just_retired_waits_for_it_uncancelled()
    {
        // A send landing as the turn it replaces finishes on its own, scripted so it
        // is reached on every run: the first turn is let go, and finishes and retires
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

        await h.Runner.SubmitAsync(chatId, "first").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
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
        }, TestContext.Current.CancellationToken);

        await h.Runner.SubmitAsync(chatId, "second").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await second.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Volatile.Write(ref reading, false);
        await reader.WaitAsync(Patience, TestContext.Current.CancellationToken);
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
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Equal(new[] { firstTurnId, secondTurnId }, h.Completed.Select(c => c.TurnId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_read_during_a_hand_over_is_told_the_replacement_and_the_old_completion_leaves_it_alone()
    {
        // While a send is replacing a running turn the chat holds both, and what a
        // read is told then decides whether the client survives the hand-over: told
        // the outgoing turn, it goes back to a turn that is ending, and takes that
        // turn's completion — which is still to come — as the chat falling idle. So
        // the answer has to be the replacement, from the moment it is attached.
        var chatId = Guid.NewGuid();
        var first = new BlockingTurn();
        var second = new BlockingTurn();
        var firstTurnId = Guid.Empty;
        var starts = 0;
        Guid? readMidHandOver = null;
        Guid? announcedWhenRead = null;
        ChatTurnRunner? runner = null;
        ConcurrentQueue<(Guid TurnId, bool Interrupted)>? completed = null;

        var h = NewRunner(
            (_, message, ct) => (message == "first" ? first : second).RunAsync(ct),
            onStarting: (_, _) =>
            {
                // The second send is attached but not installed: exactly the instant
                // this test is about. The first turn is still running — nothing has
                // cancelled it yet — so a reader could be told either turn.
                if (Interlocked.Increment(ref starts) == 2)
                {
                    readMidHandOver = runner!.ActiveTurnId(chatId);
                    announcedWhenRead = completed!.TryPeek(out var done) ? done.TurnId : null;
                }

                return Task.CompletedTask;
            });
        runner = h.Runner;
        completed = h.Completed;

        await h.Runner.SubmitAsync(chatId, "first").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await first.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        firstTurnId = Assert.IsType<Guid>(h.Runner.ActiveTurnId(chatId));

        // The outgoing turn stays in finalization for a moment after it is cancelled,
        // as a real one does while it persists its interrupted reply.
        first.Release.TrySetResult();
        await h.Runner.SubmitAsync(chatId, "second").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await second.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var secondTurnId = Assert.IsType<Guid>(h.Runner.ActiveTurnId(chatId));

        Assert.NotEqual(firstTurnId, secondTurnId);
        Assert.Null(announcedWhenRead);
        Assert.Equal(secondTurnId, readMidHandOver);

        // The old turn's completion has been and gone by now — the send waits for it —
        // and the chat still has the replacement, so that completion cannot be read as
        // this chat going idle.
        Assert.Equal(new[] { firstTurnId }, h.Completed.Select(c => c.TurnId));
        Assert.Equal(new[] { true }, h.Completed.Select(c => c.Interrupted));
        Assert.Equal(secondTurnId, h.Runner.ActiveTurnId(chatId));
        Assert.Equal(new[] { firstTurnId, secondTurnId }, h.Started);

        second.Release.TrySetResult();
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task A_send_answers_with_the_turn_it_started_even_when_it_replaces_one()
    {
        // The sender is told which turn it started, so it does not have to wait for
        // the announcement to know — that broadcast can be dropped, and a sender that
        // cannot name its turn cannot tell its events from the displaced turn's.
        var chatId = Guid.NewGuid();
        var first = new BlockingTurn();
        var second = new BlockingTurn();
        var h = NewRunner((_, message, ct) => (message == "first" ? first : second).RunAsync(ct));

        var firstTurn = await h.Runner.SubmitAsync(chatId, "first").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await first.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(firstTurn, h.Runner.ActiveTurnId(chatId));
        Assert.Equal(new[] { firstTurn }, h.Started);

        // The interrupting send answers with the replacement, not with the turn it
        // displaced, and that is the turn the chat then has.
        first.Release.TrySetResult();
        var secondTurn = await h.Runner.SubmitAsync(chatId, "second").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await second.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.NotEqual(firstTurn, secondTurn);
        Assert.Equal(secondTurn, h.Runner.ActiveTurnId(chatId));
        Assert.Equal(new[] { firstTurn, secondTurn }, h.Started);
        Assert.Equal(new[] { firstTurn }, h.Completed.Select(c => c.TurnId));

        second.Release.TrySetResult();
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Equal(new[] { firstTurn, secondTurn }, h.Completed.Select(c => c.TurnId));
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
        await h.Runner.SubmitAsync(chatId, "two").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await turn.Running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var live = Assert.IsType<Guid>(h.Runner.ActiveTurnId(chatId));

        // Into a busy chat: the turn it would have replaced is neither cancelled nor
        // displaced, so the chat still reads as busy with that turn.
        failStart = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.SubmitAsync(chatId, "three"));
        Assert.Equal(live, h.Runner.ActiveTurnId(chatId));
        Assert.False(turn.Finalizing.Task.IsCompleted);

        turn.Release.TrySetResult();
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Equal(0, h.Runner.ActiveTurnCount);
        Assert.Equal(0, h.Runner.GateCount);

        // One turn ever ran, and it is the one that was announced.
        Assert.Equal(new[] { live }, h.Completed.Select(c => c.TurnId));
        Assert.Contains(live, h.Started);
        Assert.Empty(h.Log.Entries);
    }
}
