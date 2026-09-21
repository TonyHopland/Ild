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
///
/// <para>It is also the source of truth for whether a chat has a turn in flight,
/// and the only thing that can name that turn: it sees turns that end without the
/// service saying anything at all (a chat deleted under a running turn). So every
/// turn it starts announces itself once and reports itself finished once under the
/// same id, however it ends, and it reads as having that turn in flight until it
/// does — a cancelled turn still has its interrupted reply to persist.</para>
/// </summary>
public sealed class ChatTurnRunner : IChatTurnRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IChatNotifier _notifier;
    private readonly ILogger<ChatTurnRunner> _log;

    private sealed class ActiveTurn(CancellationTokenSource cts)
    {
        // What the client recognises this turn by, so a completion from a turn that
        // has already been replaced can be told from its successor's.
        public Guid Id { get; } = Guid.NewGuid();

        public CancellationTokenSource Cts { get; } = cts;

        // Assigned right after the turn is put in the map, under the session's gate,
        // so the only readers — SubmitAsync and CancelActiveAsync, also under that
        // gate — never see the gap between the two.
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<Guid, ActiveTurn> _active = new();
    // A turn attached to a chat while it is not the live one: on its way in, between
    // a send claiming the chat and the install below, or on its way out, while a
    // stop waits for it to persist its partial reply and report itself finished. A
    // chat with work in flight is in one of the two maps at every instant, which is
    // what a reader needs and what an absent key would break. Reporting only:
    // nothing here ever cancels or disposes anything, so the ownership rules that
    // hang off `_active` are untouched.
    private readonly ConcurrentDictionary<Guid, ActiveTurn> _transition = new();
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

    public ChatTurnRunner(IServiceScopeFactory scopes, IChatNotifier notifier, ILogger<ChatTurnRunner> log)
    {
        _scopes = scopes;
        _notifier = notifier;
        _log = log;
    }

    public async Task SubmitAsync(Guid chatSessionId, string userMessage, string? openWorkItemId = null, string? openLoopDocument = null)
    {
        var gate = await EnterAsync(chatSessionId).ConfigureAwait(false);
        try
        {
            var turn = new ActiveTurn(new CancellationTokenSource());

            // Attached before anything else is read or written, so from here the
            // chat has a turn at every instant. The turn it is replacing may retire
            // itself at any moment — Retire runs outside this gate — and without
            // this the chat would read as idle between that retirement and the
            // install below.
            _transition[chatSessionId] = turn;

            // Install as the live turn without ever taking the key out: either it
            // replaces what is there, which is what makes this thread the owner of
            // that turn's cancellation and disposal, or there is nothing to replace
            // and it is added. Retire disposes only the turn it manages to remove
            // itself, so ownership is never shared and Cancel never runs against a
            // source someone else has disposed.
            ActiveTurn? displaced = null;
            ActiveTurn? retiring = null;
            while (true)
            {
                if (_active.TryGetValue(chatSessionId, out var prev))
                {
                    if (_active.TryUpdate(chatSessionId, turn, prev))
                    {
                        displaced = prev;
                        break;
                    }

                    // It went out from under us, so Retire owns it: still wait for
                    // it below, but neither cancel nor dispose it.
                    retiring = prev;
                    continue;
                }

                if (_active.TryAdd(chatSessionId, turn)) break;
            }

            // The live map holds it now, so the attachment has done its job.
            _transition.TryRemove(new KeyValuePair<Guid, ActiveTurn>(chatSessionId, turn));

            // Announced before the outgoing turn is cancelled: its completion then
            // lands on a client that already knows a newer turn is running, so it
            // cannot be mistaken for this chat falling idle.
            await _notifier.TurnStartedAsync(chatSessionId, turn.Id).ConfigureAwait(false);

            if (displaced is not null)
                await DrainAsync(chatSessionId, displaced, cancel: true).ConfigureAwait(false);
            else if (retiring is not null)
                await DrainAsync(chatSessionId, retiring, cancel: false).ConfigureAwait(false);

            turn.Task = Task.Run(async () =>
            {
                // How the turn ended comes back from the execution itself, rather
                // than being sampled once it is over: a stop landing after the reply
                // was persisted would otherwise announce an interrupt of a reply the
                // service had already written as a complete one.
                var interrupted = false;
                try
                {
                    interrupted = await RunTurnAsync(chatSessionId, turn.Id, userMessage, openWorkItemId, openLoopDocument, turn.Cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    Retire(chatSessionId, turn);
                    await _notifier.TurnCompletedAsync(chatSessionId, turn.Id, interrupted).ConfigureAwait(false);
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

    public Guid? ActiveTurnId(Guid chatSessionId)
    {
        if (_active.TryGetValue(chatSessionId, out var turn)) return turn.Id;
        // A turn on its way in or on its way out is still this chat's turn: one has
        // yet to be installed, the other has yet to persist its interrupted reply
        // and report itself done. Either way the bubble must keep showing work.
        return _transition.TryGetValue(chatSessionId, out var attached) ? attached.Id : null;
    }

    /// <summary>The gates currently held; for the test that they do not pile up.</summary>
    internal int GateCount => _gates.Count;

    /// <summary>The turns still tracked; for the test that finished ones do not pile up.</summary>
    internal int ActiveTurnCount => _active.Count;

    // A finished turn has nothing left to cancel, so it drops itself rather than
    // waiting for a next submit that may never come. Removed by identity, so a
    // newer turn for the same chat is never the one that goes, and whoever takes
    // the entry out is the one that disposes its CancellationTokenSource.
    //
    // Called before the turn announces that it has finished, and it drops the turn
    // from both maps, because after that announcement no other will come: a reader
    // still handed this turn would hold a stop button that nothing can clear.
    // Dropping the attachment owns nothing — that map never cancels or disposes —
    // so it is unconditional, and its canceller clears it again anyway.
    private void Retire(Guid chatSessionId, ActiveTurn turn)
    {
        if (_active.TryRemove(new KeyValuePair<Guid, ActiveTurn>(chatSessionId, turn)))
            turn.Cts.Dispose();

        _transition.TryRemove(new KeyValuePair<Guid, ActiveTurn>(chatSessionId, turn));
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

    // Taking the turn out of `_active` is the claim: whoever succeeds owns its
    // cancellation and disposal, and the turn's own Retire — the only other remover
    // — can then no longer take it out, so it no longer disposes it either. That is
    // what keeps Cancel from ever running against a disposed source, so the claim
    // stays exactly where it is.
    private async Task CancelActiveAsync(Guid chatSessionId)
    {
        if (!_active.TryGetValue(chatSessionId, out var prev)) return;

        // Attached before it is claimed, never after: the turn has to
        // be in one of the two maps at every instant, and claiming first would
        // leave the chat reading as idle in between — the same remove-then-insert
        // gap this whole change exists to close.
        _transition[chatSessionId] = prev;

        // The claim, by identity so it can only ever take the turn just published.
        // Failing it means the turn retired itself first: it owns its own disposal
        // and has nothing left to cancel, and the attachment goes back out
        // because there is no drain to keep it visible for.
        if (!_active.TryRemove(new KeyValuePair<Guid, ActiveTurn>(chatSessionId, prev)))
        {
            _transition.TryRemove(new KeyValuePair<Guid, ActiveTurn>(chatSessionId, prev));
            return;
        }

        try
        {
            await DrainAsync(chatSessionId, prev, cancel: true).ConfigureAwait(false);
        }
        finally
        {
            // A backstop. The turn drops itself from here before announcing that it
            // has finished, which is what keeps a reader from being handed the id of
            // a turn whose completion has already been sent.
            _transition.TryRemove(new KeyValuePair<Guid, ActiveTurn>(chatSessionId, prev));
        }
    }

    // Cancel a displaced turn and wait for it to finalize, so its interrupted reply
    // is persisted and announced before anything else touches the same transcript.
    // <paramref name="cancel"/> is false only when the turn retired itself first: it
    // disposed its own CancellationTokenSource on the way out, and a disposed one
    // can neither be cancelled nor disposed again.
    private async Task DrainAsync(Guid chatSessionId, ActiveTurn turn, bool cancel)
    {
        try
        {
            if (cancel) turn.Cts.Cancel();
            await turn.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Interrupted chat turn for {ChatSessionId} ended with an exception", chatSessionId);
        }
        finally
        {
            if (cancel) turn.Cts.Dispose();
        }
    }

    // Returns whether the turn ended cancelled, taken while the execution is still
    // the only thing that has run: the reply's own interrupted flag is taken from
    // this same token by the service that persists it, so reading it any later can
    // contradict what the transcript already says.
    private async Task<bool> RunTurnAsync(Guid chatSessionId, Guid turnId, string userMessage, string? openWorkItemId, string? openLoopDocument, CancellationToken ct)
    {
        // The caller retires this turn when it ends, whether it finished or threw,
        // so nothing is left behind for a chat that is never used again.
        try
        {
            using var scope = _scopes.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            await chat.ExecuteTurnAsync(chatSessionId, turnId, userMessage, openWorkItemId, openLoopDocument, ct).ConfigureAwait(false);
            return ct.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chat turn failed for {ChatSessionId}", chatSessionId);
            return ct.IsCancellationRequested;
        }
    }
}
