using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>What the engine does for the pod or container every chat shares.</summary>
public interface IChatEngine
{
    /// <summary>What the pod or container every chat shares is called.</summary>
    string Name { get; }

    /// <summary>Whether the chat pod or container is up, and made from what ILD would make it from now.</summary>
    Task<bool> IsUpAsync(CancellationToken ct);

    /// <summary>
    /// Make the chat pod or container and wait until it is ready, replacing one that stopped, ended or is no longer what ILD would
    /// make. Its volume keeps the chats' folders through that. Called by one caller at a time, and reads for itself
    /// what there is to replace: <see cref="IsUpAsync"/> is called by many at once.
    /// </summary>
    Task MakeAsync(CancellationToken ct);

    /// <summary>Remove the chat pod or container, and nothing of what it mounts.</summary>
    Task RemoveAsync(CancellationToken ct);

    /// <summary>Whether the chat pod or container is there to remove, found without making it.</summary>
    Task<bool> ExistsAsync(CancellationToken ct);
}

/// <summary>
/// Keeps the chat container: makes it when a chat needs it and removes it when it has been idle. Open chat work (execs: turns,
/// commands, previews, clones; and holds) is counted. The reaper sets <c>_removing</c>, then reads the count; work adds to the
/// count, then reads the flag (both Interlocked), so one of them always sees the other.
/// </summary>
internal sealed class ChatKeeper(IChatEngine engine, TimeSpan idleTimeout, TimeProvider time, string noun, ILogger? logger)
{
    private int _open;
    private long _last;
    private int _removing;
    private int _removedForIdle;

    // Making and removing the chat container are one at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Whether the reaper is about to remove, or removing, the chat container: EnsureAsync then waits for the gate instead of trusting what it sees.
    private bool BeingRemoved => Volatile.Read(ref _removing) != 0;

    // Whether the chat container was removed for idleness and has not come back since; reads once.
    private bool TakeRemovedForIdle() => Interlocked.Exchange(ref _removedForIdle, 0) != 0;

    /// <summary>The name of the chat pod or container, made first when it is not up.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        var name = engine.Name;
        if (!BeingRemoved && await engine.IsUpAsync(ct)) return name;

        await _gate.WaitAsync(ct);
        try
        {
            if (await engine.IsUpAsync(ct)) return name;
            await engine.MakeAsync(ct);
            if (TakeRemovedForIdle()) logger?.LogInformation("The chat {Noun} {Name} is back, made for a chat after it was removed for idleness", noun, name);
            return name;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Counts chat work as open until the result is disposed.</summary>
    public ChatWork Begin()
    {
        Interlocked.Increment(ref _open);
        Interlocked.Exchange(ref _last, time.GetUtcNow().UtcTicks);
        return new ChatWork(End);
    }

    private void End()
    {
        // Stamped first, so the reaper never sees no open work together with a stale time.
        Interlocked.Exchange(ref _last, time.GetUtcNow().UtcTicks);
        Interlocked.Decrement(ref _open);
    }

    // Idle: nothing open, and the last start or end was at least the timeout ago. The first look only starts the clock, for a
    // container that was there before this ILD did.
    private bool IsIdle()
    {
        if (idleTimeout <= TimeSpan.Zero || Volatile.Read(ref _open) != 0) return false;
        var now = time.GetUtcNow().UtcTicks;
        if (Interlocked.CompareExchange(ref _last, now, 0) == 0) return false;
        return now - Interlocked.Read(ref _last) >= idleTimeout.Ticks;
    }

    /// <summary>
    /// Removes the chat container or pod when it has been idle for the timeout; its volume or claim and network stay. Under the
    /// chat's gate, so it never interleaves with <see cref="EnsureAsync"/>.
    /// </summary>
    public async Task ReapIdleAsync(CancellationToken ct)
    {
        if (!IsIdle()) return;

        var name = engine.Name;
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsIdle() || !await engine.ExistsAsync(ct)) return;

            Interlocked.Exchange(ref _removing, 1);
            try
            {
                // Work that began after the check above, and before the flag, is seen here.
                if (!IsIdle()) return;
                await engine.RemoveAsync(ct);
                Interlocked.Exchange(ref _removedForIdle, 1);
                logger?.LogInformation("Removed the chat {Noun} {Name}: it was idle for {Minutes} minutes; the next chat makes it again", noun, name, (int)idleTimeout.TotalMinutes);
            }
            finally
            {
                Interlocked.Exchange(ref _removing, 0);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Chat work counted as open by <see cref="ChatKeeper"/> until this is disposed (more than once is once).</summary>
internal sealed class ChatWork(Action? end) : IDisposable
{
    public static readonly ChatWork None = new(null);
    private Action? _end = end;

    public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
}

// Counts one chat exec as open until its command ends or the stream is disposed, whichever comes first.
internal sealed class ChatExecStream : IExecStream
{
    private readonly IExecStream _inner;
    private readonly ChatWork _work;

    public ChatExecStream(IExecStream inner, ChatWork work)
    {
        _inner = inner;
        _work = work;
        Status = Track(inner.Status);
    }

    public Stream Stdout => _inner.Stdout;
    public Stream? Stderr => _inner.Stderr;
    public Task<int?> Status { get; }

    private async Task<int?> Track(Task<int?> status)
    {
        try { return await status.ConfigureAwait(false); }
        finally { _work.Dispose(); }
    }

    public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _inner.WriteStdinAsync(data, ct);
    public Task CloseStdinAsync(CancellationToken ct = default) => _inner.CloseStdinAsync(ct);
    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) => _inner.ResizeAsync(cols, rows, ct);

    public void Dispose()
    {
        _inner.Dispose();
        _work.Dispose();
    }
}
