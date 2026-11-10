using System.Collections.Concurrent;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Slows online password guessing. Failed sign-ins are counted in memory per caller address and per user name; after
/// <see cref="FreeFailures"/> failures each further attempt must wait twice as long as the last, from one second up to
/// <see cref="MaxDelay"/>. It is a delay, never a lockout, so a run that reaches the web port cannot lock the admin out for good.
/// </summary>
public sealed class LoginThrottle
{
    public const int FreeFailures = 5;
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 10_000;
    private const int MaxKeyLength = 256;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed record Entry(int Failures, DateTimeOffset NextAllowed);

    public LoginThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>How long the caller must wait before this attempt is looked at, or null when it may go ahead.</summary>
    public TimeSpan? RetryAfter(string? address, string? username)
    {
        var now = _clock.GetUtcNow();
        var wait = TimeSpan.Zero;
        foreach (var key in Keys(address, username))
            if (_entries.TryGetValue(key, out var e) && e.NextAllowed - now is { } left && left > wait)
                wait = left;
        return wait > TimeSpan.Zero ? wait : null;
    }

    public void RecordFailure(string? address, string? username)
    {
        var now = _clock.GetUtcNow();
        if (_entries.Count >= MaxEntries) Prune(now);
        foreach (var key in Keys(address, username))
            _entries.AddOrUpdate(
                key,
                _ => Next(0, now),
                (_, old) => Next(old.Failures, now));
    }

    public void RecordSuccess(string? username)
    {
        if (!string.IsNullOrEmpty(username))
            _entries.TryRemove(UserKey(username), out _);
    }

    private static Entry Next(int failures, DateTimeOffset now)
    {
        failures++;
        return new(failures, failures < FreeFailures ? now : now + Delay(failures));
    }

    private static TimeSpan Delay(int failures)
    {
        var seconds = Math.Pow(2, Math.Min(failures - FreeFailures, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    // Entries older than the longest delay say nothing any more; dropping them keeps a flood of made-up names from growing the map.
    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, e) in _entries)
            if (now - e.NextAllowed > MaxDelay)
                _entries.TryRemove(key, out _);
    }

    private static IEnumerable<string> Keys(string? address, string? username)
    {
        if (!string.IsNullOrEmpty(address)) yield return "ip:" + address;
        if (!string.IsNullOrEmpty(username)) yield return UserKey(username);
    }

    private static string UserKey(string username)
    {
        var name = username.Trim().ToLowerInvariant();
        return "user:" + (name.Length > MaxKeyLength ? name[..MaxKeyLength] : name);
    }
}
