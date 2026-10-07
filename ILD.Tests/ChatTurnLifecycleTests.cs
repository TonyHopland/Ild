using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The runner is the source of truth for whether a chat has a turn in flight, and
/// the only thing that can name that turn: it sees turns that end without the
/// service saying anything at all. So every turn it starts must announce itself
/// exactly once and report itself finished exactly once under the same id —
/// however it ends — and the chat must never read as idle while one turn is being
/// handed over to its replacement. A gap in either leaves the bubble with a
/// working indicator and a stop button that never clear, or (the reported bug)
/// none while the agent is still working.
/// </summary>
public sealed class ChatTurnLifecycleTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private sealed record StartedTurn(Guid ChatSessionId, Guid TurnId);

    private sealed record CompletedTurn(Guid ChatSessionId, Guid TurnId, bool Interrupted);

    /// <summary>An inbox hint, with the turn a history read would have been told about as it went out.</summary>
    private sealed record ActivityHint(string UserId, Guid ChatSessionId, Guid? ActiveTurnAtHint);

    public enum TurnEnd { Finishes, Stopped, Throws }

    private sealed class RecordingChatNotifier : IChatNotifier
    {
        private readonly object _gate = new();
        private readonly List<StartedTurn> _started = new();
        private readonly List<CompletedTurn> _completed = new();
        private readonly List<ChatMessageView> _appended = new();
        private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = new();
        private readonly List<ActivityHint> _activity = new();
        private readonly List<(int Count, TaskCompletionSource Tcs)> _activityWaiters = new();

        /// <summary>What the runner answers a history read with, sampled as each inbox hint goes out.</summary>
        public Func<Guid, Guid?>? ActiveTurn { get; set; }

        /// <summary>Every inbox hint throws, as a notifier that does not swallow its own failures would.</summary>
        public bool FailActivityHints { get; set; }

        public Task MessageAppendedAsync(Guid chatSessionId, Guid turnId, ChatMessageView message)
        {
            lock (_gate) _appended.Add(message);
            return Task.CompletedTask;
        }

        public Task TurnProgressAsync(Guid chatSessionId, Guid turnId, string delta) => Task.CompletedTask;

        public Task TurnStartedAsync(Guid chatSessionId, Guid turnId)
        {
            lock (_gate) _started.Add(new StartedTurn(chatSessionId, turnId));
            return Task.CompletedTask;
        }

        public Task TurnCompletedAsync(Guid chatSessionId, Guid turnId, bool interrupted)
        {
            lock (_gate)
            {
                _completed.Add(new CompletedTurn(chatSessionId, turnId, interrupted));
                foreach (var waiter in _waiters.Where(w => w.Count <= _completed.Count).ToList())
                {
                    waiter.Tcs.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }
            return Task.CompletedTask;
        }

        public Task LoopUpdateRequestedAsync(Guid chatSessionId, string document) => Task.CompletedTask;
        public Task EditProposalsChangedAsync(Guid chatSessionId) => Task.CompletedTask;
        public Task UnreadChangedAsync(string userId, Guid chatSessionId) => Task.CompletedTask;
        public Task TitleChangedAsync(string userId, Guid chatSessionId) => Task.CompletedTask;

        public Task ActivityChangedAsync(string userId, Guid chatSessionId)
        {
            var active = ActiveTurn?.Invoke(chatSessionId);
            lock (_gate)
            {
                _activity.Add(new ActivityHint(userId, chatSessionId, active));
                foreach (var waiter in _activityWaiters.Where(w => w.Count <= _activity.Count).ToList())
                {
                    waiter.Tcs.TrySetResult();
                    _activityWaiters.Remove(waiter);
                }
            }
            if (FailActivityHints) throw new InvalidOperationException("hub down");
            return Task.CompletedTask;
        }

        // Turns run in the background, so every read is a snapshot taken under the
        // same lock the recording writes under.
        public IReadOnlyList<StartedTurn> Started { get { lock (_gate) return _started.ToList(); } }
        public IReadOnlyList<CompletedTurn> Completed { get { lock (_gate) return _completed.ToList(); } }
        public IReadOnlyList<ChatMessageView> Appended { get { lock (_gate) return _appended.ToList(); } }
        public IReadOnlyList<ActivityHint> Activity { get { lock (_gate) return _activity.ToList(); } }

        /// <summary>Completes once at least <paramref name="count"/> inbox hints have gone out.</summary>
        public Task ActivityAtLeast(int count)
        {
            lock (_gate)
            {
                if (_activity.Count >= count) return Task.CompletedTask;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _activityWaiters.Add((count, tcs));
                return tcs.Task;
            }
        }

        /// <summary>Completes once at least <paramref name="count"/> turns have reported finished.</summary>
        public Task CompletedAtLeast(int count)
        {
            lock (_gate)
            {
                if (_completed.Count >= count) return Task.CompletedTask;
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, tcs));
                return tcs.Task;
            }
        }
    }

    /// <summary>
    /// Stands in for the turn body. Only the two ExecuteTurnAsync overloads take
    /// part in running a turn; everything else on the interface is the REST
    /// surface, which the runner never calls.
    /// </summary>
    private sealed class ScriptedChatService : IChatService
    {
        private readonly Func<Guid, string, CancellationToken, Task> _run;
        private readonly string? _owner;

        public ScriptedChatService(Func<Guid, string, CancellationToken, Task> run, string? owner = "alice")
        {
            _run = run;
            _owner = owner;
        }

        /// <summary>Every chat belongs to the one owner given, or to nobody once it is gone (null).</summary>
        public Task<string?> GetOwnerAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(_owner);

        public Task ExecuteTurnAsync(Guid chatSessionId, Guid turnId, string userMessage, CancellationToken ct)
            => _run(chatSessionId, userMessage, ct);

        public Task ExecuteTurnAsync(Guid chatSessionId, Guid turnId, string userMessage, string? openWorkItemId, string? openLoopDocument, CancellationToken ct)
            => _run(chatSessionId, userMessage, ct);

        public Task<IReadOnlyList<ChatSessionSummaryView>> ListForUserAsync(string userId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ChatSessionView?> GetByIdAsync(string userId, Guid sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsForUserAsync(string userId, Guid sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> MarkReadAsync(string userId, Guid sessionId, int sequence, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RenameAsync(string userId, Guid sessionId, string name, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> SearchForUserAsync(string userId, string query, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetFavoriteAsync(string userId, Guid sessionId, bool favorite, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ChatSessionView> StartAsync(string userId, Guid aiProviderId, IReadOnlyList<string>? tools, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string userId, Guid sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<int> DeleteAllForUserAsync(string userId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // Built through the container so the test does not pin the constructor's
    // parameter order; IServiceScopeFactory comes from the provider, the rest is
    // handed in.
    private static ChatTurnRunner NewRunner(IChatService chat, IChatNotifier notifier)
    {
        var services = new ServiceCollection().AddScoped(_ => chat).BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<ChatTurnRunner>(
            services, notifier, NullLogger<ChatTurnRunner>.Instance);
    }

    [Fact]
    public async Task A_turn_that_finishes_on_its_own_reports_started_and_completed_once_under_one_id()
    {
        var notifier = new RecordingChatNotifier();
        var runner = NewRunner(new ScriptedChatService((_, _, _) => Task.CompletedTask), notifier);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "hello");
        await notifier.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var started = Assert.Single(notifier.Started);
        var completed = Assert.Single(notifier.Completed);
        Assert.NotEqual(Guid.Empty, started.TurnId);
        Assert.Equal(chatId, started.ChatSessionId);
        Assert.Equal(chatId, completed.ChatSessionId);
        Assert.Equal(started.TurnId, completed.TurnId);
        Assert.False(completed.Interrupted);
    }

    [Fact]
    public async Task A_turn_cancelled_by_a_stop_reports_interrupted_once_under_its_started_id()
    {
        var notifier = new RecordingChatNotifier();
        var running = new TaskCompletionSource();
        // The service swallows the cancellation and finalizes the partial reply, so
        // the turn ends normally rather than throwing (ChatService.ExecuteTurnAsync).
        var runner = NewRunner(new ScriptedChatService(async (_, _, ct) =>
        {
            running.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        }), notifier);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "long one");
        await running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        await notifier.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var started = Assert.Single(notifier.Started);
        var completed = Assert.Single(notifier.Completed);
        Assert.Equal(started.TurnId, completed.TurnId);
        Assert.True(completed.Interrupted, "a turn that ended by cancellation reports interrupted");
        // The stop awaited the turn's finalization, so the chat is idle again.
        Assert.Null(runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task A_turn_whose_execution_throws_still_reports_completed_under_its_started_id()
    {
        var notifier = new RecordingChatNotifier();
        var runner = NewRunner(
            new ScriptedChatService((_, _, _) => throw new InvalidOperationException("boom")), notifier);

        await runner.SubmitAsync(Guid.NewGuid(), "hello");
        await notifier.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        var started = Assert.Single(notifier.Started);
        var completed = Assert.Single(notifier.Completed);
        Assert.Equal(started.TurnId, completed.TurnId);
    }

    [Fact]
    public async Task A_turn_for_a_chat_that_no_longer_exists_still_reports_started_and_completed()
    {
        var notifier = new RecordingChatNotifier();
        using var db = new TestDb();
        var chat = new ChatService(
            db.Context,
            db.Providers,
            Mock.Of<IAgentAdapterRegistry>(),
            notifier,
            // Nothing on this path touches the filesystem: the service returns as
            // soon as it finds no session row.
            new ChatOptions { ScratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-lifecycle-tests") },
            db.LoopRuns,
            new ChatLoopScratchpad());
        var runner = NewRunner(chat, notifier);

        await runner.SubmitAsync(Guid.NewGuid(), "hello");
        await notifier.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The service had nothing to say about a chat that is not there — which is
        // exactly why the turn's own completion cannot be left to it.
        Assert.Empty(notifier.Appended);
        var started = Assert.Single(notifier.Started);
        var completed = Assert.Single(notifier.Completed);
        Assert.Equal(started.TurnId, completed.TurnId);
    }

    [Fact]
    public async Task ActiveTurnId_names_the_running_turn_and_is_null_for_a_chat_with_none()
    {
        var notifier = new RecordingChatNotifier();
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runner = NewRunner(new ScriptedChatService(async (_, _, _) =>
        {
            running.TrySetResult();
            await release.Task;
        }), notifier);
        var chatId = Guid.NewGuid();

        Assert.Null(runner.ActiveTurnId(chatId));

        await runner.SubmitAsync(chatId, "go");
        await running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(Assert.Single(notifier.Started).TurnId, runner.ActiveTurnId(chatId));

        release.SetResult();
        await notifier.CompletedAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);
        // A turn retires before it announces that it finished, so by then the chat
        // already reads as idle.
        Assert.Null(runner.ActiveTurnId(chatId));
    }

    [Fact]
    public async Task A_chat_never_reads_as_idle_while_one_turn_hands_over_to_the_next()
    {
        var notifier = new RecordingChatNotifier();
        var firstRunning = new TaskCompletionSource();
        var firstCancelled = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var secondRunning = new TaskCompletionSource();
        var releaseSecond = new TaskCompletionSource();

        var runner = NewRunner(new ScriptedChatService(async (_, message, ct) =>
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
        }), notifier);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var firstTurn = runner.ActiveTurnId(chatId);
        Assert.NotNull(firstTurn);

        // A message interrupts rather than queues, so this send cancels the turn
        // above and starts its replacement.
        var interruptingSend = runner.SubmitAsync(chatId, "two");

        await firstCancelled.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        // The window the bug lives in: the outgoing turn is cancelled and still
        // finalizing, and the client is about to be told it ended.
        Assert.NotNull(runner.ActiveTurnId(chatId));

        releaseFirst.SetResult();
        await interruptingSend.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await secondRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var secondTurn = runner.ActiveTurnId(chatId);
        Assert.NotNull(secondTurn);
        Assert.NotEqual(firstTurn, secondTurn);

        // Two turns, each announced once; the replaced one reported itself
        // finished exactly once, under its own id rather than its successor's.
        Assert.Equal([firstTurn, secondTurn], notifier.Started.Select(s => (Guid?)s.TurnId));
        var completed = Assert.Single(notifier.Completed);
        Assert.Equal(firstTurn, completed.TurnId);
        Assert.True(completed.Interrupted);

        releaseSecond.SetResult();
        await notifier.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(secondTurn, notifier.Completed[1].TurnId);
        Assert.False(notifier.Completed[1].Interrupted);
    }

    [Theory]
    [InlineData(TurnEnd.Finishes)]
    [InlineData(TurnEnd.Stopped)]
    [InlineData(TurnEnd.Throws)]
    public async Task A_turn_hints_the_owners_inbox_once_visible_and_again_once_retired(TurnEnd end)
    {
        var notifier = new RecordingChatNotifier();
        var running = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runner = NewRunner(new ScriptedChatService(async (_, _, ct) =>
        {
            running.TrySetResult();
            if (end == TurnEnd.Stopped)
            {
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                return;
            }
            await release.Task;
            if (end == TurnEnd.Throws) throw new InvalidOperationException("boom");
        }, owner: "alice"), notifier);
        notifier.ActiveTurn = runner.ActiveTurnId;
        var chatId = Guid.NewGuid();

        var turnId = await runner.SubmitAsync(chatId, "go");
        await running.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await notifier.ActivityAtLeast(1).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // A history read on this hint already sees the chat busy with this turn.
        var atStart = Assert.Single(notifier.Activity);
        Assert.Equal("alice", atStart.UserId);
        Assert.Equal(chatId, atStart.ChatSessionId);
        Assert.Equal(turnId, atStart.ActiveTurnAtHint);

        if (end == TurnEnd.Stopped)
            await runner.InterruptAsync(chatId).WaitAsync(Patience, TestContext.Current.CancellationToken);
        else
            release.SetResult();
        await notifier.ActivityAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // And on this one, idle again.
        var hints = notifier.Activity;
        Assert.Equal(2, hints.Count);
        Assert.Equal("alice", hints[1].UserId);
        Assert.Equal(chatId, hints[1].ChatSessionId);
        Assert.Null(hints[1].ActiveTurnAtHint);
        Assert.Single(notifier.Started);
    }

    [Fact]
    public async Task A_chat_with_no_owner_hints_no_inbox_and_its_turns_run_as_usual()
    {
        var notifier = new RecordingChatNotifier();
        var firstRunning = new TaskCompletionSource();
        var runner = NewRunner(new ScriptedChatService(async (_, message, ct) =>
        {
            if (message != "one") return;
            firstRunning.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        }, owner: null), notifier);
        var chatId = Guid.NewGuid();

        await runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        // The send that replaces a turn waits for it to finish to the end, so once it
        // returns the first turn has done everything it was ever going to do.
        await runner.SubmitAsync(chatId, "two").WaitAsync(Patience, TestContext.Current.CancellationToken);
        await notifier.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Empty(notifier.Activity);
        Assert.Equal(2, notifier.Started.Count);
        Assert.Equal(notifier.Started.Select(s => s.TurnId), notifier.Completed.Select(c => c.TurnId));
    }

    [Fact]
    public async Task A_failing_inbox_hint_fails_neither_the_send_nor_the_turn()
    {
        var notifier = new RecordingChatNotifier { FailActivityHints = true };
        var firstRunning = new TaskCompletionSource();
        var releaseSecond = new TaskCompletionSource();
        var runner = NewRunner(new ScriptedChatService(async (_, message, ct) =>
        {
            if (message == "one")
            {
                firstRunning.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
                return;
            }
            await releaseSecond.Task;
        }), notifier);
        var chatId = Guid.NewGuid();

        var first = await runner.SubmitAsync(chatId, "one");
        await firstRunning.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var second = await runner.SubmitAsync(chatId, "two").WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(second, runner.ActiveTurnId(chatId));

        releaseSecond.SetResult();
        await notifier.CompletedAtLeast(2).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal([first, second], notifier.Started.Select(s => s.TurnId));
        Assert.Equal([first, second], notifier.Completed.Select(c => c.TurnId));
        Assert.True(notifier.Completed[0].Interrupted);
        Assert.False(notifier.Completed[1].Interrupted);
        Assert.Null(runner.ActiveTurnId(chatId));
        // The hints were attempted, and their failure went no further.
        Assert.NotEmpty(notifier.Activity);
    }
}
