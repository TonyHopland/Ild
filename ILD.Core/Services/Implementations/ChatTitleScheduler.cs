using System.Collections.Concurrent;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Runs each title job in its own DI scope, off the turn that scheduled it (like
/// <see cref="ChatTurnRunner"/>'s turns), and gives up on a title model that has
/// not answered within the timeout. Disposing it cancels the jobs still running.
/// </summary>
public sealed class ChatTitleScheduler : IChatTitleScheduler, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ChatTitleScheduler> _log;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public ChatTitleScheduler(IServiceScopeFactory scopes, ILogger<ChatTitleScheduler> log, TimeSpan? timeout = null)
    {
        _scopes = scopes;
        _log = log;
        _timeout = timeout ?? DefaultTimeout;
    }

    public Task Schedule(Guid chatSessionId, string? openWorkItemId)
    {
        var job = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _running.GetOrAdd(chatSessionId, job.Task);
        if (running != job.Task) return running;

        _ = Task.Run(async () =>
        {
            try
            {
                await GenerateAsync(chatSessionId, openWorkItemId).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(new KeyValuePair<Guid, Task>(chatSessionId, job.Task));
                job.SetResult();
            }
        });
        return job.Task;
    }

    private async Task GenerateAsync(Guid chatSessionId, string? openWorkItemId)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(_timeout);
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ChatTitleGenerator>()
                .GenerateAsync(chatSessionId, openWorkItemId, timeout.Token).ConfigureAwait(false);
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
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
