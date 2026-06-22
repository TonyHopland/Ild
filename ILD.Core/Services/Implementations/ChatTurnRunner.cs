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

    private sealed record ActiveTurn(CancellationTokenSource Cts, Task Task);

    private readonly ConcurrentDictionary<Guid, ActiveTurn> _active = new();
    // One gate per session serializes the cancel-previous-then-start-new sequence
    // so two near-simultaneous submits can't both think they are first.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public ChatTurnRunner(IServiceScopeFactory scopes, ILogger<ChatTurnRunner> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public async Task SubmitAsync(Guid chatSessionId, string userMessage, string? openWorkItemId = null)
    {
        var gate = _gates.GetOrAdd(chatSessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CancelActiveAsync(chatSessionId).ConfigureAwait(false);

            var cts = new CancellationTokenSource();
            var task = Task.Run(() => RunTurnAsync(chatSessionId, userMessage, openWorkItemId, cts.Token));
            _active[chatSessionId] = new ActiveTurn(cts, task);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task InterruptAsync(Guid chatSessionId)
    {
        var gate = _gates.GetOrAdd(chatSessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CancelActiveAsync(chatSessionId).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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

    private async Task RunTurnAsync(Guid chatSessionId, string userMessage, string? openWorkItemId, CancellationToken ct)
    {
        // A completed turn is left in the active map until the next submit/interrupt
        // clears it; cancelling an already-finished task is a harmless no-op, so the
        // serializing gate is the only state that needs explicit upkeep.
        try
        {
            using var scope = _scopes.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            await chat.ExecuteTurnAsync(chatSessionId, userMessage, openWorkItemId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chat turn failed for {ChatSessionId}", chatSessionId);
        }
    }
}
