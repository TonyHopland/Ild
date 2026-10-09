using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// One lock per Chat Schedule, held for one firing's decide-and-start or for one
/// edit or delete, so a firing acts on the schedule as it stands and an edit never
/// lands under a firing. The lock is only ever tried, never waited on: whoever
/// finds it held turns away at once. Schedules never share a lock.
/// </summary>
public sealed class ChatScheduleLocks
{
    private readonly ConcurrentDictionary<Guid, Held> _held = new();

    /// <summary>The lock, released by disposing it; null at once when someone holds it.</summary>
    public IDisposable? TryEnter(Guid scheduleId)
    {
        var held = new Held(this, scheduleId);
        return _held.TryAdd(scheduleId, held) ? held : null;
    }

    private sealed class Held(ChatScheduleLocks owner, Guid scheduleId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner._held.TryRemove(new KeyValuePair<Guid, Held>(scheduleId, this));
        }
    }
}
