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

    private static ChatTurnRunner NewRunner(
        Func<string, CancellationToken, Task> run,
        ConcurrentQueue<Guid> started,
        ConcurrentQueue<(Guid TurnId, bool Interrupted)> completed)
    {
        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string message, string? _, string? _, CancellationToken ct) => run(message, ct));

        var notifier = new Mock<IChatNotifier>();
        notifier.Setup(n => n.TurnStartedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Returns((Guid _, Guid turnId) =>
            {
                started.Enqueue(turnId);
                return Task.CompletedTask;
            });
        notifier.Setup(n => n.TurnCompletedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()))
            .Returns((Guid _, Guid turnId, bool interrupted) =>
            {
                completed.Enqueue((turnId, interrupted));
                return Task.CompletedTask;
            });

        var services = new ServiceCollection().AddScoped(_ => chat.Object).BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ChatTurnRunner>(
            services, notifier.Object, NullLogger<ChatTurnRunner>.Instance);
    }

    [Fact]
    public async Task Every_send_that_replaces_a_running_turn_still_reports_it_finished_once()
    {
        const int sends = 200;
        var started = new ConcurrentQueue<Guid>();
        var completed = new ConcurrentQueue<(Guid TurnId, bool Interrupted)>();
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
            started,
            completed);
        var chatId = Guid.NewGuid();

        for (var i = 0; i < sends; i++)
        {
            await runner.SubmitAsync(chatId, $"message {i}").WaitAsync(Patience);
            await running.WaitAsync(Patience);
            Assert.NotNull(runner.ActiveTurnId(chatId));
        }

        await runner.InterruptAsync(chatId).WaitAsync(Patience);

        await WaitUntilAsync(
            () => completed.Count == sends, $"every one of the {sends} turns should report itself finished");
        Assert.Equal(sends, started.Count);
        Assert.Equal(sends, started.Distinct().Count());
        Assert.Equal(started.OrderBy(id => id), completed.Select(c => c.TurnId).OrderBy(id => id));
        Assert.All(completed, c => Assert.True(c.Interrupted, "a turn ended by cancellation reports interrupted"));
        Assert.Null(runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task A_replacement_turn_waits_for_the_turn_it_interrupts_to_finish()
    {
        var started = new ConcurrentQueue<Guid>();
        var completed = new ConcurrentQueue<(Guid TurnId, bool Interrupted)>();
        var firstRunning = new TaskCompletionSource();
        var firstEnded = false;
        var secondSawTheFirstEnd = false;

        var runner = NewRunner(
            async (message, ct) =>
            {
                if (message == "one")
                {
                    firstRunning.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                    // Stands in for persisting and announcing the interrupted reply,
                    // which the replacement must not race.
                    await Task.Delay(20, CancellationToken.None);
                    Volatile.Write(ref firstEnded, true);
                    return;
                }

                secondSawTheFirstEnd = Volatile.Read(ref firstEnded);
            },
            started,
            completed);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience);
        await runner.SubmitAsync(chatId, "two").WaitAsync(Patience);

        await WaitUntilAsync(() => completed.Count == 2, "both turns should report themselves finished");
        Assert.True(secondSawTheFirstEnd, "the replacement turn started before the one it interrupted had finished");
        Assert.Equal(2, started.Distinct().Count());
    }

    [Fact]
    public async Task A_stop_that_arrives_after_the_turn_ended_adds_no_second_completion()
    {
        var started = new ConcurrentQueue<Guid>();
        var completed = new ConcurrentQueue<(Guid TurnId, bool Interrupted)>();
        var runner = NewRunner((_, _) => Task.CompletedTask, started, completed);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "quick one");
        await WaitUntilAsync(() => completed.Count == 1, "the turn should report itself finished");

        // Stopping an idle chat is a no-op; a second completion here would clear the
        // indicator of whichever turn is running by the time it lands.
        await runner.InterruptAsync(chatId).WaitAsync(Patience);

        Assert.Single(started);
        Assert.Single(completed);
        Assert.Null(runner.ActiveTurnId(chatId));
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
