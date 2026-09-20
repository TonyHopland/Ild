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
    private static ChatTurnRunner NewRunner()
    {
        var chat = new Mock<IChatService>();
        chat.Setup(c => c.ExecuteTurnAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection().AddScoped(_ => chat.Object).BuildServiceProvider();
        // The runner announces each turn's start and end itself; what it announces
        // is ChatTurnLifecycleTests' subject, so here it goes nowhere.
        return ActivatorUtilities.CreateInstance<ChatTurnRunner>(
            services, Mock.Of<IChatNotifier>(), NullLogger<ChatTurnRunner>.Instance);
    }

    [Fact]
    public async Task Chats_that_came_and_went_leave_no_gate_behind()
    {
        var runner = NewRunner();

        for (var i = 0; i < 500; i++)
        {
            var chatId = Guid.NewGuid();
            await runner.SubmitAsync(chatId, "hello");
            await runner.InterruptAsync(chatId);
            await runner.DeleteAsync(chatId, () => Task.CompletedTask);
        }

        Assert.Equal(0, runner.GateCount);
    }

    [Fact]
    public async Task Turns_that_finished_leave_no_entry_behind()
    {
        var runner = NewRunner();

        for (var i = 0; i < 200; i++)
            await runner.SubmitAsync(Guid.NewGuid(), "hello");

        // The turns run in the background, so give them a moment to end on their own:
        // nothing here interrupts or deletes, which is what used to clear the map.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (runner.ActiveTurnCount > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

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
