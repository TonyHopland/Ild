using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class ManagedAgentUsage : IManagedAgentUsage
{
    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IDisposable? Acquire(string? providerType)
    {
        if (ManagedAgentCatalog.Find(providerType) is not { } agent) return null;

        lock (_lock)
        {
            _active[agent.Key] = _active.GetValueOrDefault(agent.Key) + 1;
        }
        return new Lease(this, agent.Key);
    }

    public bool TryRunWhenIdle(string agentKey, Action action)
    {
        lock (_lock)
        {
            if (_active.ContainsKey(agentKey)) return false;
            action();
            return true;
        }
    }

    private void Release(string agentKey)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(agentKey, out var count)) return;
            if (count <= 1) _active.Remove(agentKey);
            else _active[agentKey] = count - 1;
        }
    }

    private sealed class Lease(ManagedAgentUsage owner, string agentKey) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(agentKey);
        }
    }
}
