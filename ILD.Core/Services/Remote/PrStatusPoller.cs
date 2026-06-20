using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Wakes the <see cref="PrStatusPoller"/> out-of-band (e.g. when the heartbeat
/// interval setting changes) so the new cadence takes effect immediately.
/// </summary>
public interface IPrStatusPoller
{
    void Pulse();
}

/// <summary>
/// Background heartbeat: on every pass, fetches a fresh PR snapshot for each run
/// parked at a PR node awaiting merge, persists it, and fires the highest-priority
/// connected custom edge on any state that newly became true. Modelled on
/// <see cref="WorkItemScheduler"/>: the cadence is read live from
/// <see cref="ISchedulerSettingsService"/> each pass and an external
/// <see cref="Pulse"/> coalesces with the timer. The polling logic itself lives
/// in <see cref="IPrStatusPollService"/> so it can be unit-tested in isolation.
/// </summary>
public sealed class PrStatusPoller : BackgroundService, IPrStatusPoller
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PrStatusPoller> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _pulse = new(0, 1);

    public PrStatusPoller(IServiceScopeFactory scopes, ILogger<PrStatusPoller> log, TimeProvider time)
    {
        _scopes = scopes;
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
            var delay = TimeSpan.FromSeconds(AppSettingKeys.DefaultPrHeartbeatSeconds);
            try
            {
                using var scope = _scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISchedulerSettingsService>();
                delay = TimeSpan.FromSeconds(Math.Max(1, await settings.GetPrHeartbeatSecondsAsync(stoppingToken)));

                var poll = scope.ServiceProvider.GetRequiredService<IPrStatusPollService>();
                await poll.PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PR heartbeat pass failed; will retry in {Delay}", delay);
            }

            await WaitForNextPassAsync(delay, stoppingToken);
        }
    }

    private async Task WaitForNextPassAsync(TimeSpan delay, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(delay, _time, linked.Token);
        var pulse = _pulse.WaitAsync(linked.Token);
        try
        {
            await Task.WhenAny(timer, pulse);
        }
        finally
        {
            // Cancel so the loser unwinds and a later Pulse is not absorbed by an
            // orphaned waiter parked in the semaphore queue (see WorkItemScheduler).
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
