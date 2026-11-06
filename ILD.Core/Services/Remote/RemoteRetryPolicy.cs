namespace ILD.Core.Services.Remote;

/// <summary>Waits between retries of a failed push or forge call; replaceable so tests need not sleep.</summary>
public interface IRemoteRetryDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class RemoteRetryDelay : IRemoteRetryDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

/// <summary>
/// How long a node waits before retrying a temporary push or forge failure:
/// three retries, about 15s, 60s and 180s apart, each jittered by up to ±20%.
/// <para>
/// The node waits in place and stays Running instead of parking. Parking would
/// move the work item to Human Feedback for every wait, which is the state this
/// retry exists to avoid, and the waits are short enough that holding a worker
/// for them is cheap: a step never waits longer than <see cref="MaxTotalWait"/>.
/// </para>
/// </summary>
public static class RemoteRetryPolicy
{
    private static readonly TimeSpan[] Delays =
        { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180) };

    /// <summary>Longest a server's Retry-After can stretch one wait.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(3);

    /// <summary>Most a single step (the push, or creating the pull request) waits in all; a retry that would pass it is not made.</summary>
    public static readonly TimeSpan MaxTotalWait = TimeSpan.FromMinutes(5);

    public static int MaxRetries => Delays.Length;

    /// <summary>The wait before retry number <paramref name="retry"/> (1-based).</summary>
    public static TimeSpan DelayFor(int retry, string? error)
    {
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        var scheduled = TimeSpan.FromSeconds(Delays[retry - 1].TotalSeconds * jitter);
        var wait = RemoteFailureClassifier.RetryAfter(error) is { } after && after > scheduled ? after : scheduled;
        return wait > MaxWait ? MaxWait : wait;
    }
}
