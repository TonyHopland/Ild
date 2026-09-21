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
/// still running. These tests hold the runner to that, and to the rule the drain
/// window sits on — the turn is taken out of the live map exactly once, and
/// whoever took it out is the only one that cancels or disposes it, so nothing can
/// cancel a source another thread has already disposed.
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

    private static Harness NewRunner(Func<string, CancellationToken, Task> run)
    {
        var started = new ConcurrentQueue<Guid>();
        var completed = new ConcurrentQueue<(Guid, bool)>();

        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string message, string? _, string? _, CancellationToken ct) => run(message, ct));

        var notifier = new Mock<IChatNotifier>();
        notifier.Setup(n => n.TurnStartedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns((Guid _, Guid turnId) => { started.Enqueue(turnId); return Task.CompletedTask; });
        notifier.Setup(n => n.TurnCompletedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()))
            .Returns((Guid _, Guid turnId, bool interrupted) => { completed.Enqueue((turnId, interrupted)); return Task.CompletedTask; });

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
        var h = NewRunner((_, ct) => turn.RunAsync(ct));
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
        var h = NewRunner(async (message, ct) =>
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
        var h = NewRunner((_, ct) => turn.RunAsync(ct));
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
        var h = NewRunner((_, ct) => turn.RunAsync(ct));
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
        var h = NewRunner((_, _) => Task.CompletedTask);

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
