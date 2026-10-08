using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// One lock per schedule, serializing its firings with its edits, Run now and
/// delete, so a firing acts on the schedule as it stands and never writes a stale
/// next firing over an edit. Schedules never share a lock, so one held up blocks
/// no other.
/// </summary>
public sealed class ChatScheduleLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> EnterAsync(Guid scheduleId, CancellationToken ct)
    {
        var semaphore = _locks.GetOrAdd(scheduleId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Held(semaphore);
    }

    /// <summary>The lock if nobody holds it, else null at once.</summary>
    public IDisposable? TryEnter(Guid scheduleId)
    {
        var semaphore = _locks.GetOrAdd(scheduleId, _ => new SemaphoreSlim(1, 1));
        return semaphore.Wait(0) ? new Held(semaphore) : null;
    }

    /// <summary>
    /// Drops a deleted schedule's lock. Whoever still holds or waits for it finds
    /// the schedule gone once they have it.
    /// </summary>
    public void Forget(Guid scheduleId) => _locks.TryRemove(scheduleId, out _);

    private sealed class Held(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
