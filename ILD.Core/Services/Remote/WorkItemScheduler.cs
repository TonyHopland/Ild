using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Configuration block for <see cref="WorkItemScheduler"/>. Read from
/// the <c>WorkItemServer</c> configuration section. Cadence values are
/// transport details; user-facing scheduler controls (max concurrent,
/// paused) live in the <c>AppSettings</c> table.
/// </summary>
public sealed class WorkItemSchedulerOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan GracePollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public bool Enabled { get; set; }
}

/// <summary>
/// Reports the scheduler being stuck at its concurrency cap. That is a state
/// rather than an event — it persists for as many passes as the slots stay
/// taken, and one item parked at a human gate puts the loop on a 5s grace
/// interval — so announcing every blocked pass would bury the signal in
/// hundreds of identical lines an hour. Entering the state and any change of
/// who is holding the slots is news, at Information; the passes in between are
/// the same line at Debug. One template either way, so an operator greps one
/// shape of line whether they want the transitions or the whole stall.
///
/// Stateful across passes, which is why it belongs to the scheduler and not to
/// the coordinator: a poll pass is scoped, derives everything it knows from
/// live runs, and deliberately remembers nothing.
/// </summary>
public sealed class CapStallReporter
{
    private readonly ILogger _log;

    /// <summary>
    /// Compared as a set, not a sequence: whether two passes report the same
    /// holders is not the coordinator's ordering to decide, and a reporter that
    /// announced whenever the order shifted would be the spam it exists to
    /// prevent.
    /// </summary>
    private HashSet<string>? _announced;

    public CapStallReporter(ILogger log) => _log = log;

    /// <summary>
    /// Say whatever <paramref name="result"/> warrants about the cap, and
    /// remember it for the next pass. Silent unless the pass was blocked.
    /// </summary>
    public void Report(PollCycleResult result, int maxConcurrent)
    {
        if (!result.BlockedByCap)
        {
            _announced = null;
            return;
        }

        var holders = new HashSet<string>(result.SlotHolders, StringComparer.Ordinal);
        var isNews = _announced == null || !_announced.SetEquals(holders);
        _announced = holders;

        _log.Log(isNews ? LogLevel.Information : LogLevel.Debug,
            "Scheduler at the concurrency cap ({MaxConcurrent}): Ready work is waiting while slots are held by work items {SlotHolders}",
            maxConcurrent, string.Join(", ", result.SlotHolders));
    }

    /// <summary>
    /// Forget what was last announced, so a board still stuck on the same slots
    /// is announced again rather than traced. For when the scheduler loses sight
    /// of the state — a failed pass, or the poller being switched off and back
    /// on — after which "still stuck, and here is who" is news again.
    /// </summary>
    public void Reset() => _announced = null;
}

/// <summary>
/// Unified scheduler: periodic remote poll plus on-demand wakeups via
/// <see cref="IWorkItemScheduler.Pulse"/>. Replaces RemoteWorkItemPoller.
/// All user-tunable knobs (max concurrent runs, paused) are read from
/// <see cref="ISchedulerSettingsService"/> on every pass so the UI can
/// retune the scheduler live.
/// </summary>
public sealed class WorkItemScheduler : BackgroundService, IWorkItemScheduler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<WorkItemSchedulerOptions> _options;
    private readonly ILogger<WorkItemScheduler> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _pulse = new(0, 1);

    public WorkItemScheduler(
        IServiceScopeFactory scopes,
        IOptionsMonitor<WorkItemSchedulerOptions> options,
        ILogger<WorkItemScheduler> log,
        TimeProvider time)
    {
        _scopes = scopes;
        _options = options;
        _log = log;
        _time = time;
    }

    public void Pulse()
    {
        // Coalesced wakeup: if already signalled, skip.
        try { _pulse.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var capStalls = new CapStallReporter(_log);

        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var delay = opts.PollInterval;

            if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.BaseUrl))
            {
                // Switched off: whatever we last saw about the cap is no longer
                // something we are watching.
                capStalls.Reset();
                await WaitForNextPassAsync(opts.PollInterval, stoppingToken);
                continue;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISchedulerSettingsService>();
                // Pause only suppresses auto-promotion of Ready items into Running;
                // the rest of the pass (heartbeats, WaitingForIld resumes,
                // closing runs behind items the server has finished, grace
                // polling) keeps running so the system stays live.
                var isPaused = await settings.GetIsPausedAsync(stoppingToken);
                var maxConcurrent = await settings.GetMaxConcurrentAsync(stoppingToken);
                var coord = scope.ServiceProvider.GetRequiredService<IRemoteWorkItemCoordinator>();
                var serverOpts = new WorkItemServerOptions { BaseUrl = opts.BaseUrl, ApiKey = opts.ApiKey };
                var result = await coord.RunPollCycleAsync(serverOpts, maxConcurrent, claimReadyItems: !isPaused, stoppingToken);
                if (result.HasActiveHumanFeedback) delay = opts.GracePollInterval;
                if (result.Claimed.Count > 0 || result.Resumed.Count > 0 || result.EscalatedToHumanFeedback.Count > 0)
                {
                    _log.LogInformation(
                        "Scheduler pass: claimed {Claimed}, resumed {Resumed}, escalated {Escalated}",
                        result.Claimed.Count, result.Resumed.Count, result.EscalatedToHumanFeedback.Count);
                }

                capStalls.Report(result, maxConcurrent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The pass told us nothing, so the next one that is blocked is
                // news again rather than a repeat.
                capStalls.Reset();
                _log.LogWarning(ex, "Scheduler pass failed; will retry in {Delay}", delay);
            }

            await WaitForNextPassAsync(delay, stoppingToken);
        }
    }

    private async Task WaitForNextPassAsync(TimeSpan delay, CancellationToken ct)
    {
        // Race the timer against an external Pulse so local events (e.g.
        // a work item flipping to Done) wake the scheduler immediately.
        // Using a linked CTS with a TimeProvider-aware delay keeps tests
        // that fake time deterministic.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(delay, _time, linked.Token);
        var pulse = _pulse.WaitAsync(linked.Token);
        try
        {
            await Task.WhenAny(timer, pulse);
        }
        finally
        {
            // Always cancel so the loser unwinds. Without this, a timer-win
            // leaves the WaitAsync parked in the semaphore's FIFO queue, and
            // the next Pulse() is consumed by that orphan instead of waking
            // the next iteration's waiter.
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
