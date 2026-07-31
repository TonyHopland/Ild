namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The one place anything asks "is this process on its way out?".
///
/// Not the host's own signals, because those answer it differently per
/// component: a <c>BackgroundService</c>'s stopping token is cancelled in its
/// own <c>StopAsync</c>, and hosted services stop in reverse registration
/// order — so while the drain is already parking runs, the scheduler's token is
/// still uncancelled and its pass would happily claim another work item.
/// <c>IHostApplicationLifetime.ApplicationStopping</c> fires once, before any
/// <c>StopAsync</c>, so a flag raised from there is the single moment every
/// component observes together.
/// </summary>
public interface IShutdownState
{
    /// <summary>True once the host has begun stopping. Never goes back to false.</summary>
    bool IsStopping { get; }

    /// <summary>Cancelled at the same moment <see cref="IsStopping"/> flips, for callers that want to await it.</summary>
    CancellationToken Stopping { get; }

    /// <summary>Raise the flag. Idempotent — the host may stop programmatically without the lifetime callback firing.</summary>
    void SignalStopping();
}

/// <summary>
/// Cancellation-token-source-backed <see cref="IShutdownState"/>, registered as
/// a singleton so every consumer shares one flag.
/// </summary>
public sealed class ShutdownState : IShutdownState
{
    /// <summary>
    /// A shared instance that is never signalled, so components taking
    /// <see cref="IShutdownState"/> as an optional constructor parameter behave
    /// exactly as they did before shutdown draining existed when nobody injects
    /// one (tests, and any other direct construction).
    /// </summary>
    public static readonly IShutdownState NeverStopping = new ShutdownState();

    private readonly CancellationTokenSource _cts = new();

    public bool IsStopping => _cts.IsCancellationRequested;

    public CancellationToken Stopping => _cts.Token;

    public void SignalStopping()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}
