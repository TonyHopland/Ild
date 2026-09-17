using System.Collections.Concurrent;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Singleton that serializes a chat session's turns and implements the interrupt
/// primitive (cancel in-flight + resume same session with the new message). Each
/// turn runs in its own DI scope so the scoped <see cref="IChatService"/> /
/// <c>DbContext</c> are not shared across the background turn boundary.
/// </summary>
public sealed class ChatTurnRunner : IChatTurnRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ChatTurnRunner> _log;

    private sealed class ActiveTurn(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;

        // Assigned right after the turn is put in the map, under the session's gate,
        // so the only reader — CancelActiveAsync, also under that gate — never sees
        // the gap between the two.
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<Guid, ActiveTurn> _active = new();
    // One gate per session serializes the cancel-previous-then-start-new sequence
    // so two near-simultaneous submits can't both think they are first. A gate is
    // held only for that sequence, so it is dropped again as soon as no one is in
    // or waiting for it — otherwise every chat id ever used would keep one for the
    // lifetime of the process.
    private readonly ConcurrentDictionary<Guid, Gate> _gates = new();

    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Holders;
        public bool Dropped;
    }

    public ChatTurnRunner(IServiceScopeFactory scopes, ILogger<ChatTurnRunner> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task SubmitAsync(Guid chatSessionId, string userMessage, string? openWorkItemId = null, string? openLoopDocument = null)
    {
        var gate = await EnterAsync(chatSessionId).ConfigureAwait(false);
        try
        {
            await CancelActiveAsync(chatSessionId).ConfigureAwait(false);

            var turn = new ActiveTurn(new CancellationTokenSource());
            _active[chatSessionId] = turn;
            turn.Task = Task.Run(async () =>
            {
                try
                {
                    await RunTurnAsync(chatSessionId, userMessage, openWorkItemId, openLoopDocument, turn.Cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    Retire(chatSessionId, turn);
                }
            });
        }
        finally
        {
            Leave(chatSessionId, gate);
        }
    }

    public async Task InterruptAsync(Guid chatSessionId)
    {
        var gate = await EnterAsync(chatSessionId).ConfigureAwait(false);
        try
        {
            await CancelActiveAsync(chatSessionId).ConfigureAwait(false);
        }
        finally
        {
            Leave(chatSessionId, gate);
        }
    }

    public async Task DeleteAsync(Guid chatSessionId, Func<Task> delete)
    {
        var gate = await EnterAsync(chatSessionId).ConfigureAwait(false);
        try
        {
            await CancelActiveAsync(chatSessionId).ConfigureAwait(false);
            await delete().ConfigureAwait(false);
        }
        finally
        {
            Leave(chatSessionId, gate);
        }
    }

    /// <summary>The gates currently held; for the test that they do not pile up.</summary>
    internal int GateCount => _gates.Count;

    /// <summary>The turns still tracked; for the test that finished ones do not pile up.</summary>
    internal int ActiveTurnCount => _active.Count;

    // A finished turn has nothing left to cancel, so it drops itself rather than
    // waiting for a next submit that may never come. Removed by identity, so a
    // newer turn for the same chat is never the one that goes, and whoever takes
    // the entry out is the one that disposes its CancellationTokenSource.
    private void Retire(Guid chatSessionId, ActiveTurn turn)
    {
        if (_active.TryRemove(new KeyValuePair<Guid, ActiveTurn>(chatSessionId, turn)))
            turn.Cts.Dispose();
    }

    private async Task<Gate> EnterAsync(Guid chatSessionId)
    {
        while (true)
        {
            var gate = _gates.GetOrAdd(chatSessionId, _ => new Gate());
            lock (gate)
            {
                // Dropped between the lookup and here: that one is gone, take the next.
                if (gate.Dropped) continue;
                gate.Holders++;
            }

            await gate.Semaphore.WaitAsync().ConfigureAwait(false);
            return gate;
        }
    }

    private void Leave(Guid chatSessionId, Gate gate)
    {
        gate.Semaphore.Release();
        lock (gate)
        {
            if (--gate.Holders > 0) return;
            gate.Dropped = true;
            _gates.TryRemove(chatSessionId, out _);
        }
    }

    private async Task CancelActiveAsync(Guid chatSessionId)
    {
        if (!_active.TryRemove(chatSessionId, out var prev)) return;
        try
        {
            prev.Cts.Cancel();
            await prev.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Interrupted chat turn for {ChatSessionId} ended with an exception", chatSessionId);
        }
        finally
        {
            prev.Cts.Dispose();
        }
    }

    private async Task RunTurnAsync(Guid chatSessionId, string userMessage, string? openWorkItemId, string? openLoopDocument, CancellationToken ct)
    {
        // The caller retires this turn when it ends, whether it finished or threw,
        // so nothing is left behind for a chat that is never used again.
        try
        {
            using var scope = _scopes.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            await chat.ExecuteTurnAsync(chatSessionId, userMessage, openWorkItemId, openLoopDocument, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chat turn failed for {ChatSessionId}", chatSessionId);
        }
    }
}
