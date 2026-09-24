using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The runner keeps one gate per chat session to serialize submit, interrupt and
/// delete. A long-lived server runs thousands of chats, so a gate that outlives
/// its session would pile up for the lifetime of the process.
/// </summary>
public sealed class ChatTurnRunnerGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // Each turn's completion releases one count, so a test awaits exactly the turns
    // it started rather than polling for them to go.
    private static ChatTurnRunner NewRunner(
        Func<string, CancellationToken, Task>? run = null, SemaphoreSlim? completions = null)
    {
        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, Guid _, string message, string? _, string? _, CancellationToken ct)
                => run is null ? Task.CompletedTask : run(message, ct));
        var notifier = new Mock<IChatNotifier>();
        notifier.Setup(n => n.TurnCompletedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()))
            .Callback(() => completions?.Release())
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection().AddScoped(_ => chat.Object).BuildServiceProvider();
        // The runner announces each turn's start and end itself; what it announces
        // is ChatTurnLifecycleTests' subject, so here it only counts completions.
        return ActivatorUtilities.CreateInstance<ChatTurnRunner>(
            services, notifier.Object, NullLogger<ChatTurnRunner>.Instance);
    }

    [Fact]
    public async Task Chats_that_came_and_went_leave_no_gate_behind()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new SemaphoreSlim(0);
        var runner = NewRunner(async (message, ct) =>
        {
            if (message != "long one") return;
            running.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        }, completions);

        // Stopped while its turn is still running.
        var busy = Guid.NewGuid();
        await runner.SubmitAsync(busy, "long one");
        await running.Task.WaitAsync(Patience);
        await runner.InterruptAsync(busy);
        await runner.DeleteAsync(busy, () => Task.CompletedTask);
        Assert.True(await completions.WaitAsync(Patience), "a turn never reported finished");

        // Stopped after its turn had already reported itself finished.
        var finished = Guid.NewGuid();
        await runner.SubmitAsync(finished, "hello");
        Assert.True(await completions.WaitAsync(Patience), "a turn never reported finished");
        await runner.InterruptAsync(finished);
        await runner.DeleteAsync(finished, () => Task.CompletedTask);

        Assert.Equal(0, runner.GateCount);
    }

    [Fact]
    public async Task Turns_that_finished_leave_no_entry_behind()
    {
        var completions = new SemaphoreSlim(0);
        var runner = NewRunner(completions: completions);

        for (var i = 0; i < 3; i++)
            await runner.SubmitAsync(Guid.NewGuid(), "hello");

        // Nothing here interrupts or deletes, which is what used to clear the map.
        // A turn retires before it announces that it finished, so once all three
        // have announced there is nothing left to wait for.
        for (var i = 0; i < 3; i++)
            Assert.True(await completions.WaitAsync(Patience), "a turn never reported finished");

        Assert.Equal(0, runner.ActiveTurnCount);
        Assert.Equal(0, runner.GateCount);
    }

    [Fact]
    public async Task A_delete_still_waits_for_a_submit_that_holds_the_gate()
    {
        var runner = NewRunner();
        var chatId = Guid.NewGuid();
        var deleteStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        // Holds the gate until released, with the delete queued behind it.
        var holding = runner.DeleteAsync(chatId, async () =>
        {
            deleteStarted.SetResult();
            await release.Task;
        });
        await deleteStarted.Task;

        var second = runner.DeleteAsync(chatId, () => Task.CompletedTask);
        Assert.False(second.IsCompleted, "the second delete ran while the first held the gate");
        Assert.Equal(1, runner.GateCount);

        release.SetResult();
        await holding;
        await second;

        Assert.Equal(0, runner.GateCount);
    }
}
