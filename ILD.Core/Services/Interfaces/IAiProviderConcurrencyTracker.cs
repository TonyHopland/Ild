using System.Collections.Concurrent;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// In-memory counter of in-flight AI node executions per <see cref="ILD.Data.Entities.AiProvider"/>.
/// <c>parallelism == 0</c> means unlimited capacity.
/// </summary>
public interface IAiProviderConcurrencyTracker
{
    bool HasCapacity(Guid providerId, int parallelism);
    void Enter(Guid providerId);
    void Exit(Guid providerId);
    int ActiveCount(Guid providerId);
}

public sealed class AiProviderConcurrencyTracker : IAiProviderConcurrencyTracker
{
    private readonly ConcurrentDictionary<Guid, int> _active = new();
    private readonly object _lock = new();

    public bool HasCapacity(Guid providerId, int parallelism)
    {
        if (parallelism <= 0) return true;
        return _active.GetValueOrDefault(providerId) < parallelism;
    }

    public void Enter(Guid providerId)
    {
        lock (_lock)
        {
            _active[providerId] = _active.GetValueOrDefault(providerId) + 1;
        }
    }

    public void Exit(Guid providerId)
    {
        lock (_lock)
        {
            var cur = _active.GetValueOrDefault(providerId);
            if (cur <= 1) _active.TryRemove(providerId, out _);
            else _active[providerId] = cur - 1;
        }
    }

    public int ActiveCount(Guid providerId) => _active.GetValueOrDefault(providerId);
}
