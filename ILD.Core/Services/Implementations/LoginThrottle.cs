namespace ILD.Core.Services.Implementations;

/// <summary>
/// Slows online password guessing. Sign-in attempts are counted in memory per caller address and, for a caller inside a run network
/// (which can reach ILD's web port and guess), per user name as well. After <see cref="FreeAttempts"/> attempts each further one must wait
/// twice as long as the last, from one second up to <see cref="MaxDelay"/>. The user-name wait applies only to run networks, so a run
/// cannot hold the operator out by guessing; it is a delay, never a lockout, and an address outside a run network is judged by its own
/// attempts only. An attempt is counted when it begins, not when it fails, so a burst sent in parallel cannot pass together.
/// </summary>
public sealed class LoginThrottle
{
    public const int FreeAttempts = 5;
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 10_000;
    private const int MaxKeyLength = 256;

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();

    private readonly record struct Entry(int Attempts, DateTimeOffset NextAllowed);

    public LoginThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Claims an attempt. Null when it may go ahead (and is counted as a failure until <see cref="Succeeded"/>), otherwise how long the
    /// caller must wait. <paramref name="byUserName"/> is true for a caller in a run network.
    /// </summary>
    public TimeSpan? TryBegin(string? address, string? username, bool byUserName)
    {
        var now = _clock.GetUtcNow();
        var keys = Keys(address, username, byUserName);
        lock (_gate)
        {
            var wait = TimeSpan.Zero;
            foreach (var key in keys)
                if (_entries.TryGetValue(key, out var e) && e.NextAllowed - now is var left && left > wait)
                    wait = left;
            if (wait > TimeSpan.Zero) return wait;

            if (_entries.Count >= MaxEntries) Prune(now);
            foreach (var key in keys)
            {
                var attempts = _entries.TryGetValue(key, out var e) ? e.Attempts + 1 : 1;
                _entries[key] = new(attempts, attempts < FreeAttempts ? now : now + Delay(attempts));
            }
            return null;
        }
    }

    /// <summary>The attempt signed in: what it counted against its address and user name is given back.</summary>
    public void Succeeded(string? address, string? username, bool byUserName)
    {
        lock (_gate)
            foreach (var key in Keys(address, username, byUserName))
                _entries.Remove(key);
    }

    private static TimeSpan Delay(int attempts)
    {
        var seconds = Math.Pow(2, Math.Min(attempts - FreeAttempts, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    // Entries older than the longest delay say nothing any more; dropping them keeps a flood of made-up names from growing the map.
    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, e) in _entries.ToList())
            if (now - e.NextAllowed > MaxDelay)
                _entries.Remove(key);
    }

    private static List<string> Keys(string? address, string? username, bool byUserName)
    {
        var keys = new List<string>(2);
        if (!string.IsNullOrEmpty(address)) keys.Add("ip:" + address);
        if (byUserName && !string.IsNullOrEmpty(username))
        {
            var name = username.Trim().ToLowerInvariant();
            keys.Add("user:" + (name.Length > MaxKeyLength ? name[..MaxKeyLength] : name));
        }
        return keys;
    }
}
