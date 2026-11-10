using System.Net;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The bounds on what one client can hold open on the egress proxy or a forwarder: how long a connection may take to
/// say where it is going, how long a relay may sit without a byte in either direction, and how many connections
/// one source address, and all of them together, may have open. Local runs all connect from loopback, so there the
/// per-source cap is one shared cap.
/// </summary>
public sealed record EgressLimits(
    TimeSpan HeadTimeout,
    TimeSpan IdleTimeout,
    int MaxPerSource,
    int MaxTotal)
{
    public EgressLimits() : this(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), 256, 1024) { }
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
