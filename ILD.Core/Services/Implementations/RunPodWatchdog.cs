using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Notices a run that is <c>Running</c> while its pod or container no longer exists, and parks it as Node Failed with the pod-lost reason, so
/// the board shows what happened and a retry makes the pod again (ADR-0040). Without it such a run sits as Running until a command happens to
/// exec into the gone pod, which a command already waiting on a dead connection never does.
/// A pod is taken for gone only when two checks in a row, a minute apart, find it missing: a pod being made again is not.
/// </summary>
public sealed class RunPodWatchdog(IServiceScopeFactory scopes, IRunHost host, ILoopEngine engine, ILogger<RunPodWatchdog>? logger = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly HashSet<Guid> _missingOnce = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = Interval;
            try { await SweepOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "The check for runs whose pod is gone failed; the next one tries again");
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var running = await scope.ServiceProvider.GetRequiredService<ILoopRunStore>().GetRunningRunsAsync();
        var checkedNow = new HashSet<Guid>();
        foreach (var run in running)
        {
            // No worktree path: the environment is not made yet. A path on ILD's own disk has no pod to lose.
            if (run.IsPaused || string.IsNullOrEmpty(run.WorktreePath) || host.PredatesContainers(run.WorktreePath)) continue;
            checkedNow.Add(run.Id);
            if (await host.GetTerminationAsync(run.Id, ct: ct) is not { Kind: RunTerminationKind.PodLost } lost)
            {
                _missingOnce.Remove(run.Id);
                continue;
            }
            if (_missingOnce.Add(run.Id)) continue;
            _missingOnce.Remove(run.Id);
            logger?.LogWarning("Run {Run} is Running but its pod no longer exists; parking it", run.Id);
            await engine.ParkRunOnLostPodAsync(run.Id, lost);
        }
        _missingOnce.RemoveWhere(id => !checkedNow.Contains(id));
    }
}
