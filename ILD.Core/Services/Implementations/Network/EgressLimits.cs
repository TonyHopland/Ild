using System.Net;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The bounds on what one client can hold open on the egress proxy or a forwarder: how long a connection may take to
/// say where it is going, how long a relay may sit without a byte in either direction, and how many connections
/// one source address, and all of them together, may have open. Local runs all connect from loopback, so there the
/// per-source cap is one shared cap. The network log is one queue for every source, so each source also has a budget of
/// rows of its own: a burst, refilled per second, past which its requests are refused without a row.
/// </summary>
public sealed record EgressLimits(
    TimeSpan HeadTimeout,
    TimeSpan IdleTimeout,
    int MaxPerSource,
    int MaxTotal)
{
    public EgressLimits() : this(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), 256, 1024) { }

    public int LogRowBurst { get; init; } = 512;

    public int LogRowsPerSecond { get; init; } = 64;
}

/// <summary>What each source (a client address for the proxy, a lease for the model gateway) may still add to the network log: one row a request, refilled over time.</summary>
public sealed class LogRowBudget<TKey>(EgressLimits limits, Func<long>? milliseconds = null) where TKey : notnull
{
    private readonly Func<long> _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    private readonly Dictionary<TKey, (double Rows, long At)> _left = new();

    public bool TryTake(TKey source)
    {
        var now = _milliseconds();
        lock (_left)
        {
            var rows = _left.TryGetValue(source, out var last)
                ? Math.Min(limits.LogRowBurst, last.Rows + (now - last.At) / 1000d * limits.LogRowsPerSecond)
                : limits.LogRowBurst;
            if (_left.Count > 4096)
                foreach (var full in _left.Where(e => e.Value.Rows + (now - e.Value.At) / 1000d * limits.LogRowsPerSecond >= limits.LogRowBurst).Select(e => e.Key).ToList())
                    _left.Remove(full);
            var taken = rows >= 1;
            _left[source] = (taken ? rows - 1 : rows, now);
            return taken;
        }
    }
}

/// <summary>Counts open connections against an <see cref="EgressLimits"/>; a lease is released by disposing it.</summary>
internal sealed class ConnectionLimiter(EgressLimits limits)
{
    private readonly Dictionary<IPAddress, int> _perSource = new();
    private readonly object _gate = new();
    private int _total;

    public int OpenCount
    {
        get { lock (_gate) return _total; }
    }

    /// <summary>A lease for one more connection from <paramref name="source"/>, or null when either cap is reached.</summary>
    public IDisposable? TryAcquire(IPAddress source)
    {
        lock (_gate)
        {
            if (_total >= limits.MaxTotal) return null;
            var current = _perSource.GetValueOrDefault(source);
            if (current >= limits.MaxPerSource) return null;
            _perSource[source] = current + 1;
            _total++;
        }
        return new Lease(this, source);
    }

    private void Release(IPAddress source)
    {
        lock (_gate)
        {
            _total--;
            if (_perSource.TryGetValue(source, out var current) && current > 1) _perSource[source] = current - 1;
            else _perSource.Remove(source);
        }
    }

    private sealed class Lease(ConnectionLimiter owner, IPAddress source) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(source);
        }
    }
}
