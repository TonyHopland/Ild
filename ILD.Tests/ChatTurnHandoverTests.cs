using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Handing one turn over to its replacement is the sharp edge of the runner,
/// because a replaced turn is taken out of the map by the send that replaces it
/// rather than by itself: it must still report itself finished, under its own id,
/// or the bubble is left with an indicator and a stop button that never clear. The
/// hand-over also has to keep what it always did — the interrupted turn finishes
/// before its replacement starts appending to the same transcript — and stopping a
/// chat whose turn has already ended must stay the no-op it is.
/// </summary>
public sealed class ChatTurnHandoverTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private sealed class TurnLog
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = new();

        public ConcurrentQueue<Guid> Started { get; } = new();
        public ConcurrentQueue<(Guid TurnId, bool Interrupted)> Completed { get; } = new();

        public Task TurnStarted(Guid turnId)
        {
            Started.Enqueue(turnId);
            return Task.CompletedTask;
        }

        public Task TurnCompleted(Guid turnId, bool interrupted)
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
            return Task.CompletedTask;
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

    private static IChatNotifier NotifierFor(TurnLog log)
    {
        var notifier = new Mock<IChatNotifier>();
        notifier.Setup(n => n.TurnStartedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns((Guid _, Guid turnId) => log.TurnStarted(turnId));
        notifier.Setup(n => n.TurnCompletedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()))
            .Returns((Guid _, Guid turnId, bool interrupted) => log.TurnCompleted(turnId, interrupted));
        return notifier.Object;
    }

    private static ChatTurnRunner NewRunner(Func<string, CancellationToken, Task> run, TurnLog log)
    {
        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Guid _, string message, string? _, string? _, CancellationToken ct) => run(message, ct));

        var services = new ServiceCollection().AddScoped(_ => chat.Object).BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ChatTurnRunner>(
            services, NotifierFor(log), NullLogger<ChatTurnRunner>.Instance);
    }

    [Fact]
    public async Task Every_send_that_replaces_a_running_turn_still_reports_it_finished_once()
    {
        const int sends = 3;
        var log = new TurnLog();
        // Each turn is still running when the next send arrives, so every send but
        // the first is a hand-over: the turn it displaces is taken out of the map by
        // the send rather than by itself, and still has to report itself finished —
        // a turn that goes quiet instead leaves the bubble with an indicator and a
        // stop button that never clear.
        var running = new SemaphoreSlim(0);
        var runner = NewRunner(
            async (_, ct) =>
            {
                running.Release();
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            },
            log);
        var chatId = Guid.NewGuid();

        for (var i = 0; i < sends; i++)
        {
            await runner.SubmitAsync(chatId, $"message {i}").WaitAsync(Patience, TestContext.Current.CancellationToken);
            Assert.True(await running.WaitAsync(Patience, TestContext.Current.CancellationToken), $"turn {i} never started");
            Assert.NotNull(runner.ActiveTurnId(chatId));
        }

        await runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);

        await log.CompletedAtLeast(sends).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(sends, log.Completed.Count);
        Assert.Equal(sends, log.Started.Count);
        Assert.Equal(sends, log.Started.Distinct().Count());
        Assert.Equal(log.Started.OrderBy(id => id), log.Completed.Select(c => c.TurnId).OrderBy(id => id));
        Assert.All(log.Completed, c => Assert.True(c.Interrupted, "a turn ended by cancellation reports interrupted"));
        Assert.Null(runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task A_replacement_turn_waits_for_the_turn_it_interrupts_to_finish()
    {
        var log = new TurnLog();
        var firstRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEnded = false;
        var secondSawTheFirstEnd = false;

        var runner = NewRunner(
            async (message, ct) =>
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
            },
            log);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var send = runner.SubmitAsync(chatId, "two");
        await firstCancelled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.False(send.IsCompleted, "the send finished while the turn it interrupted was still finalizing");
        Assert.False(secondStarted.Task.IsCompleted, "the replacement started while the turn it interrupted was still finalizing");

        releaseFirst.SetResult();
        await send.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await log.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(secondSawTheFirstEnd, "the replacement turn started before the one it interrupted had finished");
        Assert.Equal(2, log.Started.Distinct().Count());
    }

    [Fact]
    public async Task A_stop_that_arrives_after_the_turn_ended_adds_no_second_completion()
    {
        var log = new TurnLog();
        var runner = NewRunner((_, _) => Task.CompletedTask, log);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "quick one");
        await log.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Stopping an idle chat is a no-op; a second completion here would clear the
        // indicator of whichever turn is running by the time it lands.
        await runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Single(log.Started);
        Assert.Single(log.Completed);
        Assert.Null(runner.ActiveTurnId(chatId));
    }

    // Disposed with the turn's DI scope, which the runner closes once the service
    // has finished with the turn — the one moment that sits after the reply is
    // persisted and before the turn is announced finished.
    private sealed class RunsWhenTheTurnsScopeCloses : IDisposable
    {
        public Action? OnDispose { get; set; }

        public void Dispose() => OnDispose?.Invoke();
    }

    [Fact]
    public async Task A_stop_landing_after_the_reply_was_persisted_does_not_report_it_interrupted()
    {
        var log = new TurnLog();

        // What the service wrote on the reply. ChatService takes it from the turn's
        // own token at the moment it persists (ChatService.ExecuteTurnAsync), so a
        // stop arriving after that leaves a reply flagged as a complete one.
        bool? persistedInterrupted = null;
        CancellationToken turnToken = default;
        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Guid _, string _, string? _, string? _, CancellationToken ct) =>
            {
                turnToken = ct;
                persistedInterrupted = ct.IsCancellationRequested;
                return Task.CompletedTask;
            });

        var hook = new RunsWhenTheTurnsScopeCloses();
        var services = new ServiceCollection()
            .AddScoped(_ => hook)
            // Resolved with the service so the scope owns it and closes it at the
            // same point, without the runner knowing anything about it.
            .AddScoped(sp => { sp.GetRequiredService<RunsWhenTheTurnsScopeCloses>(); return chat.Object; })
            .BuildServiceProvider();
        var runner = ActivatorUtilities.CreateInstance<ChatTurnRunner>(
            services, NotifierFor(log), NullLogger<ChatTurnRunner>.Instance);
        var chatId = Guid.NewGuid();

        Task? stop = null;
        hook.OnDispose = () =>
        {
            // The stop lands here, in the gap the turn used to be judged in. It is
            // not awaited: it cancels before it waits for this very turn, so waiting
            // for the token is waiting for the part that matters without waiting on
            // ourselves.
            stop = runner.InterruptAsync(chatId);
            turnToken.WaitHandle.WaitOne(Patience);
        };

        await runner.SubmitAsync(chatId, "one").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await log.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);
        await (stop ?? Task.CompletedTask).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.False(persistedInterrupted, "the reply was persisted before the stop, so it is not interrupted");
        Assert.True(turnToken.IsCancellationRequested, "the stop should have landed while the turn was ending");
        var (_, interrupted) = Assert.Single(log.Completed);
        Assert.False(interrupted, "the turn was announced interrupted though its reply was persisted as complete");
    }
}
