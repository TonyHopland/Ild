using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>
/// Removes the objects of runs that are finished or gone, on a timer. The owner Secret already takes a run's objects with it
/// when ILD deletes it; this is for what an ILD that was killed mid-run, or a cluster that was unreachable, left behind.
/// It only ever touches objects labelled with its own instance. The same sweep runs once at startup, with the other leftovers.
/// </summary>
public sealed class KubernetesSweeper(
    IRunHost host, Func<CancellationToken, Task<IReadOnlySet<Guid>>> liveIds, TimeSpan interval, ILogger<KubernetesSweeper>? logger = null)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (host is not RoutingRunHost routing) return;

        await routing.Kubernetes.CheckStorageAsync(stoppingToken);
        using var timer = new PeriodicTimer(interval);
        while (await WaitForNextAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(routing.Kubernetes, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "A sweep of the pods runs left behind failed; the next one tries again");
            }
        }
    }

    internal async Task SweepAsync(KubernetesRunHost kubernetes, CancellationToken ct)
    {
        var leftovers = await kubernetes.ListLeftoversAsync(await liveIds(ct), ct);
        foreach (var leftover in leftovers)
        {
            if (await kubernetes.DeleteLeftoverAsync(leftover, ct))
                logger?.LogInformation("Removed the objects of run {Run}, which is finished or gone", leftover.Location);
        }
    }

    private static async Task<bool> WaitForNextAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
