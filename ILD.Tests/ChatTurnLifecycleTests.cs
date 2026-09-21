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

    private sealed class RecordingChatNotifier : IChatNotifier
    {
        private readonly object _gate = new();
        private readonly List<StartedTurn> _started = new();
        private readonly List<CompletedTurn> _completed = new();
        private readonly List<ChatMessageView> _appended = new();
        private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = new();

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

        // Turns run in the background, so every read is a snapshot taken under the
        // same lock the recording writes under.
        public IReadOnlyList<StartedTurn> Started { get { lock (_gate) return _started.ToList(); } }
        public IReadOnlyList<CompletedTurn> Completed { get { lock (_gate) return _completed.ToList(); } }
        public IReadOnlyList<ChatMessageView> Appended { get { lock (_gate) return _appended.ToList(); } }

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

        public ScriptedChatService(Func<Guid, string, CancellationToken, Task> run) => _run = run;

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
        await notifier.CompletedAtLeast(1).WaitAsync(Patience);

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
        await running.Task.WaitAsync(Patience);
        await runner.InterruptAsync(chatId).WaitAsync(Patience);
        await notifier.CompletedAtLeast(1).WaitAsync(Patience);

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
        await notifier.CompletedAtLeast(1).WaitAsync(Patience);

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
        await notifier.CompletedAtLeast(1).WaitAsync(Patience);

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
        await running.Task.WaitAsync(Patience);

        Assert.Equal(Assert.Single(notifier.Started).TurnId, runner.ActiveTurnId(chatId));

        release.SetResult();
        await notifier.CompletedAtLeast(1).WaitAsync(Patience);
        // The turn drops itself as it unwinds, so give that last step a moment.
        await WaitUntilAsync(() => runner.ActiveTurnId(chatId) is null,
            "a finished turn should leave the chat reading as idle");
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
        await firstRunning.Task.WaitAsync(Patience);
        var firstTurn = runner.ActiveTurnId(chatId);
        Assert.NotNull(firstTurn);

        // A message interrupts rather than queues, so this send cancels the turn
        // above and starts its replacement.
        var interruptingSend = runner.SubmitAsync(chatId, "two");

        await firstCancelled.Task.WaitAsync(Patience);
        // The window the bug lives in: the outgoing turn is cancelled and still
        // finalizing, and the client is about to be told it ended.
        Assert.NotNull(runner.ActiveTurnId(chatId));

        releaseFirst.SetResult();
        await interruptingSend.WaitAsync(Patience);
        await secondRunning.Task.WaitAsync(Patience);

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
        await notifier.CompletedAtLeast(2).WaitAsync(Patience);
        Assert.Equal(secondTurn, notifier.Completed[1].TurnId);
        Assert.False(notifier.Completed[1].Interrupted);
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
