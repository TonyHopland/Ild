using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Fires chat schedules that have come due (ADR-0025). A pass runs every
/// <see cref="Interval"/> or at once on <see cref="Pulse"/>, and starts each due
/// schedule as its own task in its own scope, so a firing held up at any step
/// delays no other. While the scheduler is paused a due firing is recorded as
/// skipped, and the schedule fires once when it is unpaused.
/// </summary>
public sealed class ChatScheduleScheduler : BackgroundService, IChatScheduleScheduler
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<ChatScheduleScheduler> _log;
    private readonly SemaphoreSlim _pulse = new(0, 1);
    private readonly object _firingsLock = new();
    private readonly HashSet<Task> _firings = new();
    // Set while the startup sweep is still owed: the start it covers firings before.
    private DateTime? _sweepFiredBefore;

    public ChatScheduleScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<ChatScheduleScheduler> log)
    {
        _scopes = scopes;
        _time = time;
        _log = log;
    }

    public void Pulse()
    {
        try { _pulse.Release(); } catch (SemaphoreFullException) { /* already pending */ }
    }

    /// <summary>
    /// Fails the firings a restart cut off before the first pass, and before the
    /// API takes requests, so the sweep never mistakes a new firing for one of them.
    /// A sweep that fails is retried every pass, limited to firings from before this
    /// start for the same reason.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow().UtcDateTime;
        if (!await TrySweepAsync(firedBefore: null, cancellationToken))
            _sweepFiredBefore = startedAt;
        await base.StartAsync(cancellationToken);
    }

    private async Task<bool> TrySweepAsync(DateTime? firedBefore, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ChatScheduleService>().FailInterruptedFiringsAsync(firedBefore, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not fail the scheduled chat firings a restart cut off; will retry");
            return false;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Task[] firings;
        lock (_firingsLock) firings = _firings.ToArray();
        await Task.WhenAll(firings).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_sweepFiredBefore is { } before && await TrySweepAsync(before, stoppingToken))
                    _sweepFiredBefore = null;
                await StartDueFiringsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chat schedule pass failed; will retry in {Delay}", Interval);
            }

            await WaitForNextPassAsync(stoppingToken);
        }
    }

    private async Task StartDueFiringsAsync(CancellationToken ct)
    {
        List<Guid> due;
        using (var scope = _scopes.CreateScope())
        {
            var paused = await scope.ServiceProvider.GetRequiredService<ISchedulerSettingsService>().GetIsPausedAsync(ct);
            var now = _time.GetUtcNow().UtcDateTime;
            due = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatSchedules.AsNoTracking()
                .Where(s => s.Enabled && ((s.NextFireAt != null && s.NextFireAt <= now) || (!paused && s.PendingSince != null)))
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        foreach (var id in due)
        {
            lock (_firingsLock)
            {
                var firing = FireIfDueAsync(id, ct);
                _firings.Add(firing);
                _ = firing.ContinueWith(done =>
                {
                    lock (_firingsLock) _firings.Remove(done);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task FireIfDueAsync(Guid scheduleId, CancellationToken ct)
    {
        // Off the pass's thread at once, so the pass starts every firing without waiting on any.
        await Task.Yield();
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ChatScheduleService>().FireIfDueAsync(scheduleId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chat schedule {ScheduleId} failed to fire", scheduleId);
        }
    }

    // As WorkItemScheduler's: the timer raced against a Pulse, with the loser
    // cancelled and a pulse it swallowed handed back.
    private async Task WaitForNextPassAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(Interval, _time, linked.Token);
        var pulse = _pulse.WaitAsync(linked.Token);
        Task winner;
        try
        {
            winner = await Task.WhenAny(timer, pulse);
        }
        finally
        {
            try { linked.Cancel(); } catch (ObjectDisposedException) { }
        }

        await pulse.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (winner == timer && pulse.IsCompletedSuccessfully)
        {
            try { _pulse.Release(); } catch (SemaphoreFullException) { /* already pending */ }
        }
    }
}
