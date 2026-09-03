using ILD.Core.Services.Interfaces;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The opt-in half of a Provider Interruption park (ADR-0017): with
/// <c>throttle.autoResume</c> on, ILD clicks Resume for the person on a run
/// parked with <see cref="HaltReason.Throttled"/>. Off — the default — nothing
/// here touches a run and the park stays exactly what it has always been, one
/// human Resume per interruption.
///
/// <para><b>The retry is the schedule.</b> No reset time is parsed: the provider
/// states it in its own prose ("resets 9:40am (UTC)") and building a timetable
/// out of that would be ILD guessing at a contract no provider offers. Instead
/// the resume itself is the probe — a provider still throttled interrupts the
/// node again and the run simply re-parks, which is the next attempt's starting
/// line. Attempts double from <see cref="FirstRetryDelay"/> so the sequence
/// spans the hours a session window actually lasts rather than hammering the
/// first five minutes.</para>
///
/// <para><b>Bounded, because a resume is not a human touch.</b>
/// <c>ResumeFromHaltAsync</c> refills the AI traversal budget (ADR-0018) on the
/// grounds that a person acted; an automatic resume must not, or an unattended
/// run would buy a fresh budget every time the provider hiccuped. So the
/// automatic resume leaves that budget alone and is counted on the run, and
/// once <see cref="MaxAutomaticResumes"/> have been spent this stops and leaves
/// the run parked for a person — the same place it would have been all along
/// with the setting off.</para>
///
/// <para><b>Both budgets are spent per unattended stretch, not per
/// interruption.</b> The count is refilled by exactly what refills the traversal
/// budget — a human touch — and by nothing else, so a run that auto-resumes
/// twice, works for an hour and is interrupted afresh is on its third attempt
/// rather than its first: it waits longer and has fewer tries left. That is the
/// deliberate reading of the same question both counters ask. A run nobody has
/// looked at is a run whose provider is rationing it, and the answer to the
/// fifth interruption in an unattended stretch is a person, not a sixth retry.
/// One Resume from a human gives it a full set of tries again.</para>
///
/// <para><b>Never any other park.</b> Only <see cref="HaltReason.Throttled"/> is
/// eligible: a human's Halt is somebody standing at the run, a
/// <see cref="HaltReason.Shutdown"/> park is startup's to resume (ADR-0017), and
/// a <see cref="HaltReason.MaxAiTraversals"/> park is the cap this service is
/// careful not to defeat.</para>
/// </summary>
public sealed class ThrottledRunResumeSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a park waits before the retry, on a run with no automatic
    /// resumes yet spent: a provider that has just said "not now" will still say
    /// it a minute later, and the first retry is the one most likely to be
    /// wasted. Doubles per resume already spent, so a run the provider keeps
    /// stopping is asked less and less often.
    /// </summary>
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Automatic resumes a run may spend before it is left for a person, counted
    /// — like the traversal budget — since a human last touched it. Spent on one
    /// park in a row, the doubling delay makes that roughly five hours of cover,
    /// the shape of a provider session window, and then it stops rather than
    /// spinning against a limit that is not going to lift.
    /// </summary>
    private const int MaxAutomaticResumes = 5;

    /// <summary>
    /// The steering note an automatic resume carries. It reaches the agent as
    /// the next message of the continued session, and it is how the event log
    /// shows the run was picked back up by ILD rather than by a person.
    /// </summary>
    public const string AutomaticResumeNote =
        "Resuming automatically after the AI provider interrupted this step. Continue where you left off.";

    private readonly IServiceScopeFactory _scopes;
    private readonly ILoopEngine _engine;
    private readonly ILogger<ThrottledRunResumeSweeper> _log;

    public ThrottledRunResumeSweeper(
        IServiceScopeFactory scopes, ILoopEngine engine, ILogger<ThrottledRunResumeSweeper> log)
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
                _log.LogError(ex, "Throttled-run resume sweep failed");
            }
        }
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;

        // Read per sweep, not once at start: turning the setting on or off takes
        // effect within a sweep rather than at the next deploy.
        var settings = sp.GetRequiredService<ISchedulerSettingsService>();
        if (!await settings.GetThrottleAutoResumeAsync(ct)) return;

        var runStore = sp.GetRequiredService<ILoopRunStore>();
        var parked = (await runStore.GetActiveRunsAsync())
            .Where(r => r.Status == LoopRunStatus.WaitingHuman
                && r.IsHalted
                && r.HaltReason == HaltReason.Throttled
                && !r.IsPaused)
            .ToList();
        if (parked.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var run in parked)
        {
            if (ct.IsCancellationRequested) return;
            if (run.ThrottleAutoResumeCount >= MaxAutomaticResumes)
            {
                _log.LogDebug(
                    "Run {RunId} stays parked for a person: {Count} automatic resumes spent since anyone touched it",
                    run.Id, run.ThrottleAutoResumeCount);
                continue;
            }

            var parkedAt = run.UpdatedAt ?? run.StartedAt ?? run.CreatedAt;
            if (parkedAt + FirstRetryDelay * Math.Pow(2, run.ThrottleAutoResumeCount) > now) continue;

            _log.LogInformation(
                "Auto-resuming throttle-parked run {RunId} (automatic resume {Attempt} of {Max} since a human touched it, parked since {ParkedAt:o})",
                run.Id, run.ThrottleAutoResumeCount + 1, MaxAutomaticResumes, parkedAt);
            try
            {
                await _engine.ResumeFromHaltAsync(run.Id, AutomaticResumeNote, automatic: true);
            }
            catch (Exception ex)
            {
                // Per run, like the watchdog: one run's failed resume must not
                // strand the others until the next sweep.
                _log.LogError(ex, "Failed to auto-resume throttle-parked run {RunId}", run.Id);
            }
        }
    }
}
