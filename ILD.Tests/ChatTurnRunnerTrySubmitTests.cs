using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A scheduled firing starts its turn only on an idle chat: one with no turn and
/// no send, stop or delete under way. Deciding that and starting the turn are one
/// step under the chat's gate, so a user's send can never land in between, and a
/// busy chat is answered at once rather than waited on.
/// </summary>
public sealed class ChatTurnRunnerTrySubmitTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private sealed class Harness
    {
        public readonly ConcurrentQueue<string> Events = new();
        public readonly ConcurrentDictionary<string, TaskCompletionSource> Hold = new();
        public readonly ConcurrentDictionary<string, CancellationToken> Tokens = new();
        public readonly ConcurrentDictionary<string, Guid> TurnIds = new();
        public Func<Task>? OnTurnStarted;
        public ChatTurnRunner Runner = null!;
        public int Executions;

        public Harness()
        {
            var chat = new Mock<IChatService>();
            chat.Setup(c => c.ExecuteTurnAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid _, Guid turnId, string message, string? _, string? _, CancellationToken ct) =>
                {
                    Interlocked.Increment(ref Executions);
                    Tokens[message] = ct;
                    TurnIds[message] = turnId;
                    Events.Enqueue($"execute:{message}");
                    if (Hold.TryGetValue(message, out var hold)) await hold.Task;
                });
            var notifier = new Mock<IChatNotifier>();
            notifier.Setup(n => n.TurnStartedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
                .Returns(() => OnTurnStarted?.Invoke() ?? Task.CompletedTask);
            notifier.Setup(n => n.TurnCompletedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()))
                .Returns(Task.CompletedTask);
            var services = new ServiceCollection().AddScoped(_ => chat.Object).BuildServiceProvider();
            Runner = ActivatorUtilities.CreateInstance<ChatTurnRunner>(
                services, notifier.Object, NullLogger<ChatTurnRunner>.Instance);
        }
    }

    [Fact]
    public async Task An_idle_chat_starts_the_turn_and_names_it_to_both_callbacks()
    {
        var h = new Harness();
        var chatId = Guid.NewGuid();
        var ended = new TaskCompletionSource<(Guid Id, bool Interrupted)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid? started = null;

        var turnId = await h.Runner.TrySubmitIfIdleAsync(chatId, "scheduled",
            async id => { await Task.CompletedTask; started = id; h.Events.Enqueue("starting"); },
            async (id, interrupted) => { await Task.CompletedTask; ended.TrySetResult((id, interrupted)); });

        Assert.NotNull(turnId);
        var (endedId, interrupted) = await ended.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(turnId, started);
        Assert.Equal(turnId, endedId);
        Assert.Equal(turnId, h.TurnIds["scheduled"]);
        Assert.False(interrupted);
        Assert.Equal(["starting", "execute:scheduled"], h.Events.ToArray());
    }

    [Fact]
    public async Task A_stopped_turn_ends_as_interrupted()
    {
        var h = new Harness();
        var chatId = Guid.NewGuid();
        var ended = new TaskCompletionSource<(Guid Id, bool Interrupted)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Hold["scheduled"] = running;

        var turnId = await h.Runner.TrySubmitIfIdleAsync(chatId, "scheduled",
            async _ => await Task.CompletedTask,
            async (id, i) => { await Task.CompletedTask; ended.TrySetResult((id, i)); });
        Assert.NotNull(turnId);
        await WaitUntilAsync(() => h.Tokens.ContainsKey("scheduled"));
        h.Tokens["scheduled"].Register(() => running.TrySetResult());

        await h.Runner.InterruptAsync(chatId);

        var (endedId, interrupted) = await ended.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(turnId, endedId);
        Assert.True(interrupted);
    }

    [Fact]
    public async Task A_chat_with_a_running_turn_is_left_alone()
    {
        var h = new Harness();
        var chatId = Guid.NewGuid();
        var release = new TaskCompletionSource();
        h.Hold["user turn"] = release;
        var userTurn = await h.Runner.SubmitAsync(chatId, "user turn");
        await WaitUntilAsync(() => h.Tokens.ContainsKey("user turn"));
        var startingCalls = 0;

        var turnId = await h.Runner.TrySubmitIfIdleAsync(chatId, "scheduled",
            async _ => { await Task.CompletedTask; Interlocked.Increment(ref startingCalls); },
            async (_, _) => await Task.CompletedTask);

        Assert.Null(turnId);
        Assert.Equal(0, startingCalls);
        Assert.False(h.Tokens["user turn"].IsCancellationRequested);
        Assert.Equal(userTurn, h.Runner.ActiveTurnId(chatId));
        Assert.Equal(1, h.Executions);
        release.SetResult();
    }

    [Fact]
    public async Task A_chat_mid_send_is_busy_and_is_not_waited_on_nor_raced()
    {
        var h = new Harness();
        var chatId = Guid.NewGuid();
        var sendHoldsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource();
        h.OnTurnStarted = async () =>
        {
            h.OnTurnStarted = null;
            sendHoldsGate.TrySetResult();
            await releaseSend.Task;
        };

        var send = h.Runner.SubmitAsync(chatId, "user turn");
        await sendHoldsGate.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var turnId = await h.Runner.TrySubmitIfIdleAsync(chatId, "scheduled",
            async _ => await Task.CompletedTask, async (_, _) => await Task.CompletedTask)
            .WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Null(turnId);
        Assert.False(send.IsCompleted, "the send finished before the firing was answered, so this proves nothing");
        releaseSend.SetResult();
        var userTurn = await send;
        await WaitUntilAsync(() => h.Tokens.ContainsKey("user turn"));
        Assert.False(h.Tokens.ContainsKey("scheduled"));
        Assert.False(h.Tokens["user turn"].IsCancellationRequested);
        Assert.Equal(userTurn, h.TurnIds["user turn"]);
    }

    [Fact]
    public async Task A_chat_mid_stop_or_mid_delete_is_busy_and_is_not_waited_on()
    {
        var h = new Harness();

        // Mid delete: the delete holds the gate until released.
        var deleting = Guid.NewGuid();
        var deleteHoldsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource();
        var delete = h.Runner.DeleteAsync(deleting, async () => { deleteHoldsGate.TrySetResult(); await releaseDelete.Task; });
        await deleteHoldsGate.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Asked twice: a refused try must give back nothing it did not take, or the
        // second one would find the gate open under the delete still holding it.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Null(await h.Runner.TrySubmitIfIdleAsync(deleting, "scheduled",
                    async _ => await Task.CompletedTask, async (_, _) => await Task.CompletedTask)
                .WaitAsync(Patience, TestContext.Current.CancellationToken));
        }
        Assert.False(delete.IsCompleted);
        releaseDelete.SetResult();
        await delete;

        // Mid stop: the stopped turn takes its time to wind down, and the stop holds
        // the gate while it waits for it.
        var stopping = Guid.NewGuid();
        var windDown = new TaskCompletionSource();
        h.Hold["slow to stop"] = windDown;
        await h.Runner.SubmitAsync(stopping, "slow to stop");
        await WaitUntilAsync(() => h.Tokens.ContainsKey("slow to stop"));
        var stop = h.Runner.InterruptAsync(stopping);
        await WaitUntilAsync(() => h.Tokens["slow to stop"].IsCancellationRequested);

        Assert.Null(await h.Runner.TrySubmitIfIdleAsync(stopping, "scheduled",
                async _ => await Task.CompletedTask, async (_, _) => await Task.CompletedTask)
            .WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.False(stop.IsCompleted, "the stop finished before the firing was answered, so this proves nothing");
        windDown.SetResult();
        await stop;

        Assert.False(h.Tokens.ContainsKey("scheduled"));
    }

    [Fact]
    public async Task A_firing_whose_start_callback_throws_starts_nothing_and_leaves_the_chat_idle()
    {
        var h = new Harness();
        var chatId = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Runner.TrySubmitIfIdleAsync(chatId, "scheduled",
            async _ => { await Task.CompletedTask; throw new InvalidOperationException("firing row could not be written"); },
            async (_, _) => await Task.CompletedTask));

        Assert.Null(h.Runner.ActiveTurnId(chatId));
        Assert.Equal(0, h.Executions);

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = await h.Runner.TrySubmitIfIdleAsync(chatId, "next firing",
            async _ => await Task.CompletedTask, async (_, _) => { await Task.CompletedTask; ended.TrySetResult(); });
        Assert.NotNull(next);
        await ended.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(0, h.Runner.ActiveTurnCount);
        Assert.Equal(0, h.Runner.GateCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            await Task.Yield();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
