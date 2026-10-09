using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Fires Chat Schedules as they come due (ADR-0026). On start it first fails the
/// firings a restart cut off; then each pass fires every schedule that is due or
/// owes a firing from a pause, and waits for the earliest next firing, at most
/// <see cref="MaxWait"/>. That cap is also how a schedule saved since, or the
/// scheduler being unpaused, is noticed.
/// </summary>
public sealed class ChatScheduleScheduler : BackgroundService
{
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    // A schedule that is due but could not fire (its lock was held) is tried
    // again after this, rather than at once in a tight loop.
    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<ChatScheduleScheduler> _log;

    public ChatScheduleScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<ChatScheduleScheduler> log)
    {
        _scopes = scopes;
        _time = time;
        _log = log;
    }

    /// <summary>One pass: every schedule due now, or owing a firing, is fired or skipped once.</summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        List<Guid> candidates;
        using (var scope = _scopes.CreateScope())
        {
            var now = _time.GetUtcNow().UtcDateTime;
            candidates = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatSchedules.AsNoTracking()
                .Where(s => s.Enabled && ((s.NextFireAt != null && s.NextFireAt <= now) || s.PendingSince != null))
                .Select(s => s.Id)
                .ToListAsync(ct);
        }

        foreach (var id in candidates)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ChatScheduleService>().FireIfDueAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogError(ex, "Chat schedule {ScheduleId} failed to fire; it is tried again next pass", id);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = MaxWait;
            try
            {
                if (!recovered)
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ChatScheduleService>().RecoverInterruptedAsync(stoppingToken);
                    recovered = true;
                }

                await RunDueAsync(stoppingToken);
                wait = await UntilNextFiringAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Chat schedule pass failed; will retry in {Delay}", wait);
            }

            try
            {
                await Task.Delay(wait, _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> UntilNextFiringAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var next = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatSchedules.AsNoTracking()
            .Where(s => s.Enabled && s.NextFireAt != null)
            .MinAsync(s => s.NextFireAt, ct);
        if (next is null) return MaxWait;
        var until = DateTime.SpecifyKind(next.Value, DateTimeKind.Utc) - _time.GetUtcNow().UtcDateTime;
        return until < MinWait ? MinWait : until > MaxWait ? MaxWait : until;
    }
}
