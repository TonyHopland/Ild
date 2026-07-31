using ILD.Core.Services.Interfaces;

namespace ILD.Api.Services;

/// <summary>
/// How long the host is willing to spend stopping, and where the two budgets
/// that bound the drain come from. Read once at startup — a value nobody can
/// change while the process runs is what makes the nesting below checkable.
///
/// <code>
/// ILD_SHUTDOWN_DRAIN_SECONDS (20s)
///   &lt; host shutdown timeout (drain + 5s)
///   &lt; supervisor grace period (compose stop_grace_period / k8s terminationGracePeriodSeconds)
/// </code>
/// </summary>
public sealed class ShutdownOptions
{
    public const string DrainSecondsVariable = "ILD_SHUTDOWN_DRAIN_SECONDS";

    /// <summary>
    /// Long enough for an agent process to die and its driving loop to write the
    /// interrupted-node bookkeeping; short enough to sit inside a Kubernetes
    /// pod's 30s default grace period with the host's own margin on top.
    /// </summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long <see cref="ILoopEngine.DrainForShutdownAsync"/> may spend parking runs.</summary>
    public TimeSpan DrainTimeout { get; init; } = DefaultDrainTimeout;

    /// <summary>
    /// What <c>HostOptions.ShutdownTimeout</c> must be set to. The drain runs
    /// <i>inside</i> the host stop, so the host has to be willing to wait
    /// strictly longer than the drain — otherwise it abandons the unwinding it
    /// just asked for.
    /// </summary>
    public TimeSpan HostShutdownTimeout => DrainTimeout + TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads <c>ILD_SHUTDOWN_DRAIN_SECONDS</c>, falling back to the default for
    /// anything malformed or non-positive — <c>0</c> in particular, which would
    /// silently restore the hard kill this exists to replace.
    /// </summary>
    public static ShutdownOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var raw = (readVariable ?? Environment.GetEnvironmentVariable)(DrainSecondsVariable);
        var seconds = double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? TimeSpan.FromSeconds(parsed)
                : DefaultDrainTimeout;
        return new ShutdownOptions { DrainTimeout = seconds };
    }
}

/// <summary>
/// Binds the host's stop to the engine's drain: raises the shared stopping flag
/// the moment the host announces it, then spends the shutdown budget parking
/// in-flight runs instead of having them killed mid-step.
///
/// Registered <b>last</b> among the hosted services on purpose — see the note at
/// its registration in <c>ServiceCollectionExtensions</c>.
/// </summary>
public sealed class GracefulRunDrainService : IHostedService
{
    private readonly ILoopEngine _engine;
    private readonly IShutdownState _shutdown;
    private readonly ShutdownOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GracefulRunDrainService> _log;

    public GracefulRunDrainService(
        ILoopEngine engine,
        IShutdownState shutdown,
        ShutdownOptions options,
        IHostApplicationLifetime lifetime,
        ILogger<GracefulRunDrainService> log)
    {
        _engine = engine;
        _shutdown = shutdown;
        _options = options;
        _lifetime = lifetime;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStopping fires once, before any hosted service's StopAsync,
        // so everything watching the flag — the launch gate, the scheduler's
        // claiming — sees "we are stopping" at the same moment, rather than each
        // learning it whenever its own stop happens to be reached.
        _lifetime.ApplicationStopping.Register(_shutdown.SignalStopping);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Defensive: a host stopped programmatically (or a test host) can reach
        // StopAsync without the lifetime callback having fired. Signalling is
        // idempotent, so raising it twice costs nothing.
        _shutdown.SignalStopping();

        try
        {
            await _engine.DrainForShutdownAsync(_options.DrainTimeout);
        }
        catch (Exception ex)
        {
            // The drain is best-effort by construction: whatever it failed to
            // park is left for the existing crash-recovery path on the next
            // start, which is strictly no worse than the hard kill it replaces.
            // Blocking process exit on it would be worse than either.
            _log.LogError(ex, "Shutdown drain failed");
        }
    }
}
