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
/// for them is cheap. The scheduled waits add up to about five minutes at most;
/// a server's Retry-After can stretch them, and <see cref="MaxTotalWait"/> bounds that.
/// </para>
/// </summary>
public static class RemoteRetryPolicy
{
    private static readonly TimeSpan[] Delays =
        { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(180) };

    /// <summary>Longest a server's Retry-After can stretch one wait.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Most a single step (the push, or creating the pull request) waits in all
    /// once a server's Retry-After is involved; a retry that would pass it is not made.
    /// The scheduled waits alone (at most about 306s with jitter) are always allowed,
    /// so jitter can never cost a retry.
    /// </summary>
    public static readonly TimeSpan MaxTotalWait = TimeSpan.FromMinutes(5);

    public static int MaxRetries => Delays.Length;

    /// <summary>The wait before retry number <paramref name="retry"/> (1-based).</summary>
    /// <param name="jitter">The multiplier on the scheduled wait, 0.8 to 1.2; random when null.</param>
    public static TimeSpan DelayFor(int retry, string? error, double? jitter = null)
    {
        jitter ??= 0.8 + Random.Shared.NextDouble() * 0.4;
        var scheduled = TimeSpan.FromSeconds(Delays[retry - 1].TotalSeconds * jitter.Value);
        var wait = RemoteFailureClassifier.RetryAfter(error) is { } after && after > scheduled ? after : scheduled;
        return wait > MaxWait ? MaxWait : wait;
    }

    /// <summary>
    /// Whether the next retry should be made. A failure with no Retry-After
    /// always gets its scheduled wait. One that carries a Retry-After is not
    /// retried when the server asks for more than <see cref="MaxWait"/>, since
    /// retrying early would only be refused again, nor when the step's waits
    /// would pass <see cref="MaxTotalWait"/>.
    /// </summary>
    public static bool AllowsRetry(TimeSpan waitedSoFar, TimeSpan nextDelay, string? error)
    {
        if (RemoteFailureClassifier.RetryAfter(error) is not { } after)
            return true;
        return after <= MaxWait && waitedSoFar + nextDelay <= MaxTotalWait;
    }
}
