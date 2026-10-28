using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The runner is the source of truth for whether a chat has a turn in flight, and
/// the only thing that can name that turn. Every turn it starts announces itself
/// exactly once and reports itself finished exactly once under the same id, however
/// it ends, and a replaced turn still reports itself — or the bubble is left with an
/// indicator and a stop button that never clear.
///
/// A stopped turn is not over the moment it is cancelled: it still has to notice,
/// persist its partial reply and report itself finished. The chat is busy for all
/// of that, and a read that says otherwise takes the stop button off a turn that is
/// still running. These tests hold the runner to that, and to the rule the window
/// sits on: a chat's turn state is one value changed only by compare-and-swap, so a
/// reader never catches it mid-change, and moving a turn out of the running slot is
/// a claim exactly one thread can win — the one that then cancels and disposes it,
/// so no thread can cancel a source another has already disposed.
/// </summary>
public sealed class ChatTurnRunnerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

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

    /// <summary>An inbox hint, with the turn a history read would have been told about as it went out.</summary>
    private sealed record ActivityHint(string UserId, Guid ChatSessionId, Guid? ActiveTurnAtHint);

    public enum TurnEnd { Finishes, Stopped, Throws }

    private sealed class Harness
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Tcs)> _completedWaiters = new();
        private readonly List<(int Count, TaskCompletionSource Tcs)> _activityWaiters = new();

        public required ChatTurnRunner Runner { get; init; }
        /// <summary>
        /// What the runner logs. A cancellation that raced a disposal is swallowed
        /// into <c>LogDebug</c> by the drain, so "nothing was logged" is how a test
        /// sees that the ownership rules held rather than merely survived.
        /// </summary>
        public required RecordingLogger<ChatTurnRunner> Log { get; init; }
        public ConcurrentQueue<Guid> Started { get; } = new();
        public ConcurrentQueue<(Guid TurnId, bool Interrupted)> Completed { get; } = new();
        public ConcurrentDictionary<Guid, Guid> StartedIn { get; } = new();
        public ConcurrentDictionary<Guid, Guid> CompletedIn { get; } = new();
        public ConcurrentQueue<ChatMessageView> Appended { get; } = new();
        public ConcurrentQueue<ActivityHint> Activity { get; } = new();

        public void RecordCompleted(Guid turnId, bool interrupted)
        {
            lock (_gate)
            {
                Completed.Enqueue((turnId, interrupted));
                Release(_completedWaiters, Completed.Count);
            }
        }

        public void RecordActivity(ActivityHint hint)
        {
            lock (_gate)
            {
                Activity.Enqueue(hint);
                Release(_activityWaiters, Activity.Count);
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> turns have reported finished.</summary>
        public Task CompletedAtLeast(int count)
        {
            lock (_gate) return Waiter(_completedWaiters, Completed.Count, count);
        }

        /// <summary>Completes once at least <paramref name="count"/> inbox hints have gone out.</summary>
        public Task ActivityAtLeast(int count)
        {
            lock (_gate) return Waiter(_activityWaiters, Activity.Count, count);
        }

        private static void Release(List<(int Count, TaskCompletionSource Tcs)> waiters, int reached)
        {
            foreach (var waiter in waiters.Where(w => w.Count <= reached).ToList())
            {
                waiter.Tcs.TrySetResult();
                waiters.Remove(waiter);
            }
        }

        private static Task Waiter(List<(int Count, TaskCompletionSource Tcs)> waiters, int reached, int count)
        {
            if (reached >= count) return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((count, tcs));
            return tcs.Task;
        }
    }

    // Disposed with the turn's DI scope, which the runner closes once the service
    // has finished with the turn — after the reply is persisted and before the turn
    // retires and is announced finished.
    private sealed class RunsWhenTheTurnsScopeCloses(Action? onDispose) : IDisposable
    {
        public void Dispose() => onDispose?.Invoke();
    }

    /// <param name="run">The turn body; ignored when <paramref name="service"/> stands in for the whole service.</param>
    /// <param name="owner">Who the chat belongs to, or nobody (null), which hints no inbox.</param>
    /// <param name="failActivityHints">Every inbox hint throws, as a notifier that does not swallow its own failures would.</param>
    private static Harness NewRunner(
        Func<Guid, string, CancellationToken, Task>? run = null,
        Action<Guid, Guid>? onCompleting = null,
        Func<Guid, Guid, Task>? onStarting = null,
        Action? onScopeClose = null,
        string? owner = null,
        bool failActivityHints = false,
        Func<IChatNotifier, IChatService>? service = null)
    {
        Harness? harness = null;

        var notifier = new Mock<IChatNotifier>();
        notifier.Setup(n => n.TurnStartedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns(async (Guid chatSessionId, Guid turnId) =>
            {
                harness!.Started.Enqueue(turnId);
                harness.StartedIn[turnId] = chatSessionId;
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
                harness!.CompletedIn[turnId] = chatSessionId;
                harness.RecordCompleted(turnId, interrupted);
                return Task.CompletedTask;
            });
        notifier.Setup(n => n.MessageAppendedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ChatMessageView>()))
            .Returns((Guid _, Guid _, ChatMessageView message) =>
            {
                harness!.Appended.Enqueue(message);
                return Task.CompletedTask;
            });
        notifier.Setup(n => n.ActivityChangedAsync(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns((string userId, Guid chatSessionId) =>
            {
                harness!.RecordActivity(new ActivityHint(userId, chatSessionId, harness.Runner.ActiveTurnId(chatSessionId)));
                if (failActivityHints) throw new InvalidOperationException("hub down");
                return Task.CompletedTask;
            });

        IChatService chat;
        if (service is not null)
            chat = service(notifier.Object);
        else
        {
            var mock = new Mock<IChatService>();
            mock.Setup(c => c.ExecuteTurnAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, Guid turnId, string message, string? _, string? _, CancellationToken ct) => run!(turnId, message, ct));
            mock.Setup(c => c.GetOwnerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(owner);
            chat = mock.Object;
        }

        var log = new RecordingLogger<ChatTurnRunner>();
        var services = new ServiceCollection()
            .AddScoped(_ => new RunsWhenTheTurnsScopeCloses(onScopeClose))
            // Resolved with the service so the scope owns the hook and closes it at
            // the same point, without the runner knowing anything about it.
            .AddScoped(sp => { sp.GetRequiredService<RunsWhenTheTurnsScopeCloses>(); return chat; })
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

    [Fact]
    public async Task A_turn_that_finishes_on_its_own_reports_started_and_completed_once_under_one_id()
    {
        var h = NewRunner((_, _, _) => Task.CompletedTask);
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "hello");
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var started = Assert.Single(h.Started);
        var (completedId, interrupted) = Assert.Single(h.Completed);
        Assert.NotEqual(Guid.Empty, started);
        Assert.Equal(chatId, h.StartedIn[started]);
        Assert.Equal(chatId, h.CompletedIn[completedId]);
        Assert.Equal(started, completedId);
        Assert.False(interrupted);
    }

    [Fact]
    public async Task A_turn_cancelled_by_a_stop_reports_interrupted_once_under_its_started_id()
    {
        var running = new TaskCompletionSource();
        // The service swallows the cancellation and finalizes the partial reply, so
        // the turn ends normally rather than throwing (ChatService.ExecuteTurnAsync).
        var h = NewRunner(async (_, _, ct) =>
        {
            running.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        });
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "long one");
        await running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var started = Assert.Single(h.Started);
        var completed = Assert.Single(h.Completed);
        Assert.Equal(started, completed.TurnId);
        Assert.True(completed.Interrupted, "a turn that ended by cancellation reports interrupted");
        // The stop awaited the turn's finalization, so the chat is idle again.
        Assert.Null(h.Runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task A_turn_whose_execution_throws_still_reports_completed_under_its_started_id()
    {
        var h = NewRunner((_, _, _) => throw new InvalidOperationException("boom"));

        await h.Runner.SubmitAsync(Guid.NewGuid(), "hello");
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var started = Assert.Single(h.Started);
        var completed = Assert.Single(h.Completed);
        Assert.Equal(started, completed.TurnId);
    }

    [Fact]
    public async Task A_turn_for_a_chat_that_no_longer_exists_still_reports_started_and_completed()
    {
        using var db = new TestDb();
        var h = NewRunner(service: notifier => new ChatService(
            db.Context,
            db.Providers,
            Mock.Of<IAgentAdapterRegistry>(),
            notifier,
            // Nothing on this path touches the filesystem: the service returns as
            // soon as it finds no session row.
            new ChatOptions { ScratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-lifecycle-tests") },
            db.LoopRuns,
            new ChatLoopScratchpad(),
            new ChatCloneCoordinator(), claudeProjects: TestClaudeProjectFolders.Instance));

        await h.Runner.SubmitAsync(Guid.NewGuid(), "hello");
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The service had nothing to say about a chat that is not there — which is
        // exactly why the turn's own completion cannot be left to it.
        Assert.Empty(h.Appended);
        var started = Assert.Single(h.Started);
        var completed = Assert.Single(h.Completed);
        Assert.Equal(started, completed.TurnId);
    }

    [Fact]
    public async Task ActiveTurnId_names_the_running_turn_and_is_null_for_a_chat_with_none()
    {
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var h = NewRunner(async (_, _, _) =>
        {
            running.TrySetResult();
            await release.Task;
        });
        var chatId = Guid.NewGuid();

        Assert.Null(h.Runner.ActiveTurnId(chatId));

        await h.Runner.SubmitAsync(chatId, "go");
        await running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(Assert.Single(h.Started), h.Runner.ActiveTurnId(chatId));

        release.SetResult();
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);
        // A turn retires before it announces that it finished, so by then the chat
        // already reads as idle.
        Assert.Null(h.Runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task Every_send_that_replaces_a_running_turn_still_reports_it_finished_once()
    {
        const int sends = 3;
        // Each turn is still running when the next send arrives, so every send but
        // the first is a hand-over: the turn it displaces is taken out of the map by
        // the send rather than by itself, and still has to report itself finished —
        // a turn that goes quiet instead leaves the bubble with an indicator and a
        // stop button that never clear.
        var running = new SemaphoreSlim(0);
        var h = NewRunner(async (_, _, ct) =>
        {
            running.Release();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        });
        var chatId = Guid.NewGuid();

        for (var i = 0; i < sends; i++)
        {
            await h.Runner.SubmitAsync(chatId, $"message {i}").WaitAsync(Patience, TestContext.Current.CancellationToken);
            Assert.True(await running.WaitAsync(Patience, TestContext.Current.CancellationToken), $"turn {i} never started");
            Assert.NotNull(h.Runner.ActiveTurnId(chatId));
        }

        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);

        await h.CompletedAtLeast(sends).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(sends, h.Completed.Count);
        Assert.Equal(sends, h.Started.Count);
        Assert.Equal(sends, h.Started.Distinct().Count());
        Assert.Equal(h.Started.OrderBy(id => id), h.Completed.Select(c => c.TurnId).OrderBy(id => id));
        Assert.All(h.Completed, c => Assert.True(c.Interrupted, "a turn ended by cancellation reports interrupted"));
        Assert.Null(h.Runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task A_replacement_turn_waits_for_the_turn_it_interrupts_to_finish()
    {
        var firstRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEnded = false;
        var secondSawTheFirstEnd = false;

        var h = NewRunner(async (_, message, ct) =>
        {
            if (message == "one")
            {
                firstRunning.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                firstCancelled.TrySetResult();
                // Stands in for persisting and announcing the interrupted reply,
                // which the replacement must not race; held until the test has
                // looked at the send.
                await releaseFirst.Task;
                Volatile.Write(ref firstEnded, true);
                return;
            }

            secondSawTheFirstEnd = Volatile.Read(ref firstEnded);
            secondStarted.TrySetResult();
        });
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var send = h.Runner.SubmitAsync(chatId, "two");
        await firstCancelled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.False(send.IsCompleted, "the send finished while the turn it interrupted was still finalizing");
        Assert.False(secondStarted.Task.IsCompleted, "the replacement started while the turn it interrupted was still finalizing");

        releaseFirst.SetResult();
        await send.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(secondSawTheFirstEnd, "the replacement turn started before the one it interrupted had finished");
        Assert.Equal(2, h.Started.Distinct().Count());
    }

    [Fact]
    public async Task A_chat_never_reads_as_idle_while_one_turn_hands_over_to_the_next()
    {
        var firstRunning = new TaskCompletionSource();
        var firstCancelled = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var secondRunning = new TaskCompletionSource();
        var releaseSecond = new TaskCompletionSource();

        var h = NewRunner(async (_, message, ct) =>
        {
            if (message == "one")
            {
                firstRunning.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                firstCancelled.TrySetResult();
                // Still persisting the interrupted reply: the chat is not idle yet.
                await releaseFirst.Task;
                return;
            }

            secondRunning.TrySetResult();
            await releaseSecond.Task;
        });
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var firstTurn = h.Runner.ActiveTurnId(chatId);
        Assert.NotNull(firstTurn);

        // A message interrupts rather than queues, so this send cancels the turn
        // above and starts its replacement.
        var interruptingSend = h.Runner.SubmitAsync(chatId, "two");

        await firstCancelled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        // The window the bug lives in: the outgoing turn is cancelled and still
        // finalizing, and the client is about to be told it ended.
        Assert.NotNull(h.Runner.ActiveTurnId(chatId));

        releaseFirst.SetResult();
        await interruptingSend.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await secondRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var secondTurn = h.Runner.ActiveTurnId(chatId);
        Assert.NotNull(secondTurn);
        Assert.NotEqual(firstTurn, secondTurn);

        // Two turns, each announced once; the replaced one reported itself
        // finished exactly once, under its own id rather than its successor's.
        Assert.Equal([firstTurn, secondTurn], h.Started.Select(s => (Guid?)s));
        var completed = Assert.Single(h.Completed);
        Assert.Equal(firstTurn, completed.TurnId);
        Assert.True(completed.Interrupted);

        releaseSecond.SetResult();
        await h.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(secondTurn, h.Completed.ElementAt(1).TurnId);
        Assert.False(h.Completed.ElementAt(1).Interrupted);
    }

    [Fact]
    public async Task A_stop_that_arrives_after_the_turn_ended_adds_no_second_completion()
    {
        var h = NewRunner((_, _, _) => Task.CompletedTask);
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "quick one");
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Stopping an idle chat is a no-op; a second completion here would clear the
        // indicator of whichever turn is running by the time it lands.
        await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Single(h.Started);
        Assert.Single(h.Completed);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task A_stop_landing_after_the_reply_was_persisted_does_not_report_it_interrupted()
    {
        // What the service wrote on the reply. ChatService takes it from the turn's
        // own token at the moment it persists (ChatService.ExecuteTurnAsync), so a
        // stop arriving after that leaves a reply flagged as a complete one.
        bool? persistedInterrupted = null;
        CancellationToken turnToken = default;
        Task? stop = null;
        ChatTurnRunner? runner = null;
        var chatId = Guid.NewGuid();

        var h = NewRunner(
            (_, _, ct) =>
            {
                turnToken = ct;
                persistedInterrupted = ct.IsCancellationRequested;
                return Task.CompletedTask;
            },
            onScopeClose: () =>
            {
                // The stop lands here, in the gap the turn used to be judged in. It is
                // not awaited: it cancels before it waits for this very turn, so waiting
                // for the token is waiting for the part that matters without waiting on
                // ourselves.
                stop = runner!.InterruptAsync(chatId);
                turnToken.WaitHandle.WaitOne(Patience);
            });
        runner = h.Runner;

        await h.Runner.SubmitAsync(chatId, "one").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);
        await (stop ?? Task.CompletedTask).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.False(persistedInterrupted, "the reply was persisted before the stop, so it is not interrupted");
        Assert.True(turnToken.IsCancellationRequested, "the stop should have landed while the turn was ending");
        var (_, interrupted) = Assert.Single(h.Completed);
        Assert.False(interrupted, "the turn was announced interrupted though its reply was persisted as complete");
    }

    [Theory]
    [InlineData(TurnEnd.Finishes)]
    [InlineData(TurnEnd.Stopped)]
    [InlineData(TurnEnd.Throws)]
    public async Task A_turn_hints_the_owners_inbox_once_visible_and_again_once_retired(TurnEnd end)
    {
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var h = NewRunner(
            async (_, _, ct) =>
            {
                running.TrySetResult();
                if (end == TurnEnd.Stopped)
                {
                    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                    return;
                }
                await release.Task;
                if (end == TurnEnd.Throws) throw new InvalidOperationException("boom");
            },
            owner: "alice");
        var chatId = Guid.NewGuid();

        var turnId = await h.Runner.SubmitAsync(chatId, "go");
        await running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.ActivityAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // A history read on this hint already sees the chat busy with this turn.
        var atStart = Assert.Single(h.Activity);
        Assert.Equal("alice", atStart.UserId);
        Assert.Equal(chatId, atStart.ChatSessionId);
        Assert.Equal(turnId, atStart.ActiveTurnAtHint);

        if (end == TurnEnd.Stopped)
            await h.Runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        else
            release.SetResult();
        await h.ActivityAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // And on this one, idle again.
        var hints = h.Activity.ToList();
        Assert.Equal(2, hints.Count);
        Assert.Equal("alice", hints[1].UserId);
        Assert.Equal(chatId, hints[1].ChatSessionId);
        Assert.Null(hints[1].ActiveTurnAtHint);
        Assert.Single(h.Started);
    }

    [Fact]
    public async Task A_chat_with_no_owner_hints_no_inbox_and_its_turns_run_as_usual()
    {
        var firstRunning = new TaskCompletionSource();
        var h = NewRunner(async (_, message, ct) =>
        {
            if (message != "one") return;
            firstRunning.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        });
        var chatId = Guid.NewGuid();

        await h.Runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        // The send that replaces a turn waits for it to finish to the end, so once it
        // returns the first turn has done everything it was ever going to do.
        await h.Runner.SubmitAsync(chatId, "two").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await h.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Empty(h.Activity);
        Assert.Equal(2, h.Started.Count);
        Assert.Equal(h.Started, h.Completed.Select(c => c.TurnId));
    }

    [Fact]
    public async Task A_failing_inbox_hint_fails_neither_the_send_nor_the_turn()
    {
        var firstRunning = new TaskCompletionSource();
        var releaseSecond = new TaskCompletionSource();
        var h = NewRunner(
            async (_, message, ct) =>
            {
                if (message == "one")
                {
                    firstRunning.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                    return;
                }
                await releaseSecond.Task;
            },
            owner: "alice",
            failActivityHints: true);
        var chatId = Guid.NewGuid();

        var first = await h.Runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var second = await h.Runner.SubmitAsync(chatId, "two").WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(second, h.Runner.ActiveTurnId(chatId));

        releaseSecond.SetResult();
        await h.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal([first, second], h.Started);
        Assert.Equal([first, second], h.Completed.Select(c => c.TurnId));
        Assert.True(h.Completed.ElementAt(0).Interrupted);
        Assert.False(h.Completed.ElementAt(1).Interrupted);
        Assert.Null(h.Runner.ActiveTurnId(chatId));
        // The hints were attempted, and their failure went no further.
        Assert.NotEmpty(h.Activity);
    }
}
