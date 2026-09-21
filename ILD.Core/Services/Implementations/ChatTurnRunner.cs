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

        // Assigned right after the turn is installed in the chat's turn state, under
        // the session's gate, so the only readers — the drains in SubmitAsync and
        // CancelActiveAsync, also under that gate — never see the gap between the
        // two. The turn itself can finish and retire before this is assigned, which
        // is harmless for the same reason: nothing can be waiting on it yet.
        public Task Task { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// Everything a chat's turn state is, as one value. <paramref name="Live"/> is
    /// the turn that is running; <paramref name="Attached"/> is a turn the chat
    /// still has but that is not running it — on its way in, between a send taking
    /// the chat and installing its turn, or on its way out, while a stop waits for
    /// the turn to persist its reply and report itself finished. Either counts as
    /// the chat being busy, and because both live in one value a reader sees the
    /// state before a change or the state after it, never a moment in between.
    /// </summary>
    private sealed record ChatTurn(ActiveTurn? Live, ActiveTurn? Attached)
    {
        public static readonly ChatTurn None = new(null, null);

        /// <summary>The turn this chat has, whichever way round it is held.</summary>
        public ActiveTurn? Current => Live ?? Attached;

        public bool IsEmpty => Live is null && Attached is null;
    }

    // One entry per chat that has a turn, changed only by compare-and-swap and
    // dropped once it holds nothing. Moving a turn out of `Live` is the claim that
    // makes cancelling and disposing it exclusive: exactly one thread can win that
    // swap, and it is the only one that touches the turn's CancellationTokenSource.
    private readonly ConcurrentDictionary<Guid, ChatTurn> _turns = new();
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
            var attaching = Swap(chatSessionId, state => state with { Attached = turn });

            // Announced before the outgoing turn is cancelled, so that turn's
            // completion lands on a client which already knows a newer turn is
            // running and cannot read it as this chat falling idle.
            //
            // A read arriving now is handed this turn's id already — it has been
            // attached since the line above, as it has to be — so the announcement
            // can be on its way out while the id is being read. That is harmless:
            // the event carries the same id, so a client that has it early applies
            // the same value twice rather than two different ones.
            //
            // Announcing here rather than after the install is what gives the
            // hand-over below a window wide enough to be scripted in a test; see
            // TurnsLeftToRetireCount.
            //
            // If announcing throws, the send fails, so the attachment goes back out
            // again: nothing else would ever clear it, and the chat would read as
            // busy from then on for a turn that is never going to run. Only a
            // notifier that throws gets here — SignalRChatNotifier swallows its own
            // failures — so this is a backstop, not a live path.
            try
            {
                await _notifier.TurnStartedAsync(chatSessionId, turn.Id).ConfigureAwait(false);
            }
            catch
            {
                Swap(chatSessionId, state => state.Attached == turn ? state with { Attached = null } : state);
                // Never installed and never run, so this thread is the only one that
                // has ever held it and the only one that can dispose it.
                turn.Cts.Dispose();
                throw;
            }

            // Installed as the live turn by one swap, which drops the attachment in
            // the same step: the chat holds this turn either way, and never neither.
            // That swap also moves whatever was live out of `Live`, and moving a turn
            // out of `Live` is what makes this thread the one that cancels and
            // disposes it — exactly one thread can win that swap.
            var installing = Swap(chatSessionId, state => state with { Live = turn, Attached = null });
            var displaced = installing.Previous.Live;

            // There was a live turn when we attached but none left to displace, so it
            // retired itself in between and owns its own disposal: wait for it below,
            // but neither cancel nor dispose it.
            var retiring = displaced is null ? attaching.Previous.Live : null;
            if (retiring is not null) Interlocked.Increment(ref _turnsLeftToRetire);

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

    // One read of one value. A turn on its way in or on its way out is still this
    // chat's turn — one has yet to be installed, the other has yet to persist its
    // interrupted reply and report itself done — and both are held together, so no
    // reader can slip between them and find a busy chat idle.
    public Guid? ActiveTurnId(Guid chatSessionId)
        => _turns.TryGetValue(chatSessionId, out var state) ? state.Current?.Id : null;

    /// <summary>The gates currently held; for the test that they do not pile up.</summary>
    internal int GateCount => _gates.Count;

    /// <summary>The turns still tracked; for the test that finished ones do not pile up.</summary>
    internal int ActiveTurnCount => _turns.Count;

    /// <summary>
    /// How many sends found the turn they were replacing already retired by the time
    /// they installed their own, and so waited for it without cancelling or disposing
    /// it. Counted so the test for that hand-over cannot pass without reaching it.
    /// </summary>
    internal int TurnsLeftToRetireCount => _turnsLeftToRetire;

    private int _turnsLeftToRetire;

    // A finished turn has nothing left to cancel, so it drops itself rather than
    // waiting for a next submit that may never come. It lets go of whichever slot
    // holds it, so a newer turn for the same chat is never the one that goes.
    //
    // Called before the turn announces that it has finished, because after that
    // announcement no other will come: a reader still handed this turn would hold a
    // stop button that nothing can clear. It disposes the source only if it was the
    // one to move the turn out of `Live` — a canceller that took it first owns it.
    private void Retire(Guid chatSessionId, ActiveTurn turn)
    {
        var retiring = Swap(chatSessionId, state => new ChatTurn(
            state.Live == turn ? null : state.Live,
            state.Attached == turn ? null : state.Attached));

        if (retiring.Previous.Live == turn) turn.Cts.Dispose();
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

    // Claims the live turn and attaches it in the same swap, so it is only ever a
    // turn that was still running at that instant — never one that has already
    // retired and possibly announced itself finished — and the chat never reads as
    // idle in between. Winning that swap is what makes this thread the one that
    // cancels and disposes it.
    private async Task CancelActiveAsync(Guid chatSessionId)
    {
        var claiming = Swap(chatSessionId, state =>
            state.Live is null ? state : new ChatTurn(null, state.Live));
        var prev = claiming.Previous.Live;
        if (prev is null) return;

        try
        {
            await DrainAsync(chatSessionId, prev, cancel: true).ConfigureAwait(false);
        }
        finally
        {
            // A backstop. The turn lets go of the attachment itself before announcing
            // that it has finished, which is what keeps a reader from being handed
            // the id of a turn whose completion has already gone out.
            Swap(chatSessionId, state => state.Attached == prev ? state with { Attached = null } : state);
        }
    }

    // Every change to a chat's turn state is one compare-and-swap of the whole
    // value, so a reader sees the state before a change or the state after it and
    // never a step within one. `change` is applied to whatever is there at the time
    // and may run more than once, so it must be a plain function of that state; the
    // pair returned is the one that was actually swapped in.
    private (ChatTurn Previous, ChatTurn Next) Swap(Guid chatSessionId, Func<ChatTurn, ChatTurn> change)
    {
        while (true)
        {
            if (_turns.TryGetValue(chatSessionId, out var current))
            {
                var next = change(current);
                if (next == current) return (current, next);
                // An entry that holds nothing is dropped rather than kept, so chats
                // that have finished with the runner do not pile up in it.
                if (next.IsEmpty)
                {
                    if (_turns.TryRemove(new KeyValuePair<Guid, ChatTurn>(chatSessionId, current)))
                        return (current, next);
                }
                else if (_turns.TryUpdate(chatSessionId, next, current))
                {
                    return (current, next);
                }

                continue;
            }

            var fresh = change(ChatTurn.None);
            if (fresh.IsEmpty) return (ChatTurn.None, fresh);
            if (_turns.TryAdd(chatSessionId, fresh)) return (ChatTurn.None, fresh);
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
