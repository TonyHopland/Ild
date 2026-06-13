using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Backstop for runs that are <see cref="LoopRunStatus.Running"/> in the
/// database but have no live driving task in this process — "orphaned" runs.
///
/// A run can become orphaned without ever passing through the engine's
/// crash handler (<c>MarkRunCrashedAsync</c>, which fires only when the
/// background loop throws a catchable exception): a launch/relaunch race that
/// leaves no owner, a crash-handler write that itself failed (it runs under a
/// swallow-all <c>TrySafe</c>), a process-level abort that never reaches the
/// catch, or any exit path that returns without a terminal transition. Once
/// orphaned, nothing recovers the run until the process restarts — the work
/// item hangs in the Running column forever. This watchdog detects and recovers
/// such runs at runtime.
///
/// <para><b>Safety.</b> The trigger is <i>absence of a live driver</i>, never
/// elapsed wall-clock time. A legitimately long-running AI node — which can run
/// for hours — always has a live task in
/// <see cref="ILoopEngine.GetActiveRunIdsAsync"/> and is therefore never
/// touched, no matter how long it has been executing. The grace window below
/// only filters the sub-second launch/relaunch transition windows where a run
/// is briefly Running in the DB before its driving task registers; it is not a
/// duration cap on the run. Recovery itself is gentle and policy-aware — it
/// re-drives, parks for review, or cancels per the run's
/// <see cref="ILD.Data.Enums.RecoveryPolicy"/> — see <see cref="IRecoveryManager"/>.</para>
/// </summary>
public sealed class StuckRunWatchdog : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    // Wait out boot-time recovery/reconciliation before the first sweep so the
    // watchdog doesn't race the startup resume of runs that were Running when
    // the process last stopped.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    // A run must be observably orphaned (Running, no driver) and untouched for
    // at least this long before we act, so we never catch a run in the brief
    // gap between a status write and its driving task registering. Far longer
    // than any real launch transition (sub-second); not a bound on run length.
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILoopEngine _engine;
    private readonly ILogger<StuckRunWatchdog> _log;

    public StuckRunWatchdog(IServiceScopeFactory scopes, ILoopEngine engine, ILogger<StuckRunWatchdog> log)
    {
        _scopes = scopes;
        _engine = engine;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = SweepInterval;

            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Stuck-run watchdog sweep failed");
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var runStore = sp.GetRequiredService<ILoopRunStore>();
        var recovery = sp.GetRequiredService<IRecoveryManager>();

        var running = await runStore.GetRunningRunsAsync();
        if (running.Count == 0) return;

        // In-process liveness: any run with a driving task is healthy and is
        // never touched, however long it has been executing.
        var active = (await _engine.GetActiveRunIdsAsync()).ToHashSet();
        var cutoff = DateTime.UtcNow - OrphanGrace;

        var recovered = 0;
        foreach (var run in running)
        {
            if (ct.IsCancellationRequested) return;
            if (run.IsPaused) continue;             // intentionally not driven
            if (active.Contains(run.Id)) continue;  // live driver — healthy

            // Skip the launch/relaunch transition window: a freshly (re)launched
            // run has a recent row write. A live long-running node also has a
            // stale UpdatedAt, but it was already excluded by the active check.
            var lastTouched = run.UpdatedAt ?? run.StartedAt ?? run.CreatedAt;
            if (lastTouched > cutoff) continue;

            _log.LogWarning(
                "Recovering orphaned run {RunId} (work item {WorkItemId}): Running with no driving task since {LastTouched:o}",
                run.Id, run.WorkItemId, lastTouched);
            try
            {
                if (await recovery.RecoverRunAsync(run.Id)) recovered++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to recover orphaned run {RunId}", run.Id);
            }
        }

        if (recovered > 0)
            _log.LogInformation("Stuck-run watchdog recovered {Count} orphaned run(s)", recovered);
    }
}
