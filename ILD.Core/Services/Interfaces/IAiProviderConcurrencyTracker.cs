namespace ILD.Core.Services.Interfaces;

/// <summary>
/// In-memory counter of in-flight AI node executions per <see cref="ILD.Data.Entities.AiProvider"/>.
/// <c>parallelism == 0</c> means unlimited capacity.
/// </summary>
public interface IAiProviderConcurrencyTracker
{
    /// <summary>Non-mutating capacity peek for callers that don't claim a slot (e.g. resume gating).</summary>
    bool HasCapacity(Guid providerId, int parallelism);

    /// <summary>Atomically check capacity and claim a slot. Returns false if at capacity.</summary>
    bool TryEnter(Guid providerId, int parallelism);

    void Exit(Guid providerId);
    int ActiveCount(Guid providerId);
}

public sealed class AiProviderConcurrencyTracker : IAiProviderConcurrencyTracker
{
    private readonly Dictionary<Guid, int> _active = new();
    private readonly object _lock = new();

    public bool HasCapacity(Guid providerId, int parallelism)
    {
        if (parallelism <= 0) return true;
        lock (_lock)
        {
            return _active.TryGetValue(providerId, out var cur) ? cur < parallelism : true;
        }
    }

    public bool TryEnter(Guid providerId, int parallelism)
    {
        lock (_lock)
        {
            var cur = _active.TryGetValue(providerId, out var v) ? v : 0;
            if (parallelism > 0 && cur >= parallelism) return false;
            _active[providerId] = cur + 1;
            return true;
        }
    }

    public void Exit(Guid providerId)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(providerId, out var cur)) return;
            if (cur <= 1) _active.Remove(providerId);
            else _active[providerId] = cur - 1;
        }
    }

    public int ActiveCount(Guid providerId)
    {
        lock (_lock)
        {
            return _active.TryGetValue(providerId, out var v) ? v : 0;
        }
    }
}
