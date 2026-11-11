using System.Collections.Concurrent;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The chats whose container is still being prepared, or whose preparation failed.
/// A chat that is in neither state is ready, or not one this process prepared.
/// </summary>
public sealed class ChatPreparations(IChatNotifier notifier, ILogger<ChatPreparations>? log = null)
{
    private readonly ConcurrentDictionary<Guid, Attempt> _attempts = new();
    private readonly ILogger _log = log ?? NullLogger<ChatPreparations>.Instance;

    private sealed class Attempt(Func<CancellationToken, Task> work)
    {
        public Func<CancellationToken, Task> Work { get; } = work;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Done => _done.Task;
        public void Finish() => _done.SetResult();
        public string? Error { get; set; }
    }

    public bool IsPreparing(Guid chatId) => _attempts.TryGetValue(chatId, out var a) && a.Error is null;

    public string? FailureOf(Guid chatId) => _attempts.TryGetValue(chatId, out var a) ? a.Error : null;

    /// <summary>Runs <paramref name="work"/> in the background, tied to no request. One that completes at once has finished when this returns.</summary>
    public void Begin(Guid chatId, Func<CancellationToken, Task> work)
        => Run(chatId, new Attempt(work));

    /// <summary>
    /// Returns once the chat is ready, or throws what the preparation failed with. A chat whose last
    /// attempt failed is prepared again, so sending another message retries.
    /// </summary>
    public async Task WaitAsync(Guid chatId, CancellationToken ct)
    {
        while (_attempts.TryGetValue(chatId, out var attempt))
        {
            if (attempt.Error is not null)
            {
                var retry = new Attempt(attempt.Work);
                if (!_attempts.TryUpdate(chatId, retry, attempt)) continue;
                Run(chatId, retry);
                attempt = retry;
            }
            await attempt.Done.WaitAsync(ct);
            if (attempt.Error is not null) throw new IOException(attempt.Error);
            return;
        }
    }

    /// <summary>Waits for a preparation in flight to end, however it ends. A failed one is forgotten.</summary>
    public async Task SettleAsync(Guid chatId)
    {
        if (!_attempts.TryGetValue(chatId, out var attempt)) return;
        await attempt.Done;
        _attempts.TryRemove(new KeyValuePair<Guid, Attempt>(chatId, attempt));
    }

    private void Run(Guid chatId, Attempt attempt)
    {
        _attempts[chatId] = attempt;
        _ = RunAsync(chatId, attempt);
    }

    private async Task RunAsync(Guid chatId, Attempt attempt)
    {
        string? error = null;
        try
        {
            await attempt.Work(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Preparing the container of chat {ChatId} failed", chatId);
            error = ex.Message;
        }

        if (error is null) _attempts.TryRemove(new KeyValuePair<Guid, Attempt>(chatId, attempt));
        else attempt.Error = error;
        attempt.Finish();

        try
        {
            if (error is null) await notifier.ChatPreparedAsync(chatId);
            else await notifier.ChatPreparationFailedAsync(chatId, error);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Announcing the preparation of chat {ChatId} failed", chatId);
        }
    }
}
