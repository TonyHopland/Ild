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
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            var delay = opts.PollInterval;

            if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.BaseUrl))
            {
                await WaitForNextPassAsync(opts.PollInterval, stoppingToken);
                continue;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISchedulerSettingsService>();
                var isPaused = await settings.GetIsPausedAsync(stoppingToken);
                if (isPaused)
                {
                    await WaitForNextPassAsync(opts.PollInterval, stoppingToken);
                    continue;
                }

                var maxConcurrent = await settings.GetMaxConcurrentAsync(stoppingToken);
                var coord = scope.ServiceProvider.GetRequiredService<IRemoteWorkItemCoordinator>();
                var serverOpts = new WorkItemServerOptions { BaseUrl = opts.BaseUrl, ApiKey = opts.ApiKey };
                var result = await coord.RunPollCycleAsync(serverOpts, maxConcurrent, stoppingToken);
                if (result.HasActiveHumanFeedback) delay = opts.GracePollInterval;
                if (result.Claimed.Count > 0 || result.Resumed.Count > 0 || result.EscalatedToHumanFeedback.Count > 0)
                {
                    _log.LogInformation(
                        "Scheduler pass: claimed {Claimed}, resumed {Resumed}, escalated {Escalated}",
                        result.Claimed.Count, result.Resumed.Count, result.EscalatedToHumanFeedback.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
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
            var done = await Task.WhenAny(timer, pulse);
            if (done == pulse) linked.Cancel(); // stop the timer
        }
        catch (OperationCanceledException) { }
    }
}
