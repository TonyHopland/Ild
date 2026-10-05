using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Runs each title job in its own scope, off the turn, within a timeout. One job runs
/// per chat; a reply arriving meanwhile waits, the newest one only, and runs next.
/// </summary>
public sealed class ChatTitleScheduler : IChatTitleScheduler, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ChatTitleScheduler> _log;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    // A chat is a key while its job runs; the value is the reply waiting to run next.
    private readonly Dictionary<Guid, Job?> _chats = new();
    private bool _disposed;

    private sealed class Job(string? openWorkItemId, int replySequence)
    {
        public string? OpenWorkItemId { get; set; } = openWorkItemId;
        public int ReplySequence { get; set; } = replySequence;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ChatTitleScheduler(IServiceScopeFactory scopes, ILogger<ChatTitleScheduler> log, TimeSpan? timeout = null)
    {
        _scopes = scopes;
        _log = log;
        _timeout = timeout ?? DefaultTimeout;
    }

    public Task Schedule(Guid chatSessionId, string? openWorkItemId, int replySequence)
    {
        lock (_gate)
        {
            if (_chats.TryGetValue(chatSessionId, out var waiting))
            {
                // Callers whose reply was overtaken share the newer reply's job.
                waiting ??= new Job(openWorkItemId, replySequence);
                waiting.OpenWorkItemId = openWorkItemId;
                waiting.ReplySequence = replySequence;
                _chats[chatSessionId] = waiting;
                return waiting.Done.Task;
            }

            var job = new Job(openWorkItemId, replySequence);
            _chats[chatSessionId] = null;
            _ = Task.Run(() => RunAsync(chatSessionId, job));
            return job.Done.Task;
        }
    }

    private async Task RunAsync(Guid chatSessionId, Job job)
    {
        while (true)
        {
            await GenerateAsync(chatSessionId, job.OpenWorkItemId, job.ReplySequence).ConfigureAwait(false);
            job.Done.SetResult();

            lock (_gate)
            {
                var next = _chats[chatSessionId];
                if (next is null || _disposed)
                {
                    _chats.Remove(chatSessionId);
                    next?.Done.SetResult();
                    return;
                }
                _chats[chatSessionId] = null;
                job = next;
            }
        }
    }

    private async Task GenerateAsync(Guid chatSessionId, string? openWorkItemId, int replySequence)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(_timeout);
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ChatTitleGenerator>()
                .GenerateAsync(chatSessionId, openWorkItemId, replySequence, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _log.LogWarning(ex, "Stopped generating a title for chat {ChatSessionId}: no answer within {Timeout}, or ILD is shutting down", chatSessionId, _timeout);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not generate a title for chat {ChatSessionId}", chatSessionId);
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
