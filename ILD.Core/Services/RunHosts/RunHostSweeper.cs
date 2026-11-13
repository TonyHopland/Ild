using ILD.Core.Services.RunHosts.Isolated;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// Removes the objects of runs that are finished or gone, on a timer. A run host deletes a run's objects when the run ends;
/// this is for what an ILD that was killed mid-run, or a cluster or socket that was unreachable, left behind.
/// It only ever touches objects labelled with its own instance. The same sweep runs once at startup, with the other leftovers.
/// Beside the sweep, it keeps the agents ILD has installed in the toolchain store, so a run or chat does not wait for them.
/// </summary>
public sealed class RunHostSweeper(
    IRunHost host, Func<CancellationToken, Task<IReadOnlySet<Guid>>> liveIds, TimeSpan interval, ILogger<RunHostSweeper>? logger = null)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (host is not RoutingRunHost routing) return;

        await routing.Isolated.CheckAsync(stoppingToken);
        await Task.WhenAll(SweepLoopAsync(routing.Isolated, stoppingToken), WarmLoopAsync(routing.Isolated, stoppingToken));
    }

    private async Task SweepLoopAsync(ExecRunHost isolated, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await WaitForNextAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(isolated, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "A sweep of what runs left behind failed; the next one tries again");
            }
        }
    }

    // Often, and from the start: an agent ILD installs or updates is in the store within a minute, not at the next run.
    private static async Task WarmLoopAsync(ExecRunHost isolated, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(WarmInterval);
        do
        {
            await isolated.WarmAgentToolsAsync(stoppingToken);
        }
        while (await WaitForNextAsync(timer, stoppingToken));
    }

    private static readonly TimeSpan WarmInterval = TimeSpan.FromMinutes(1);

    internal async Task SweepAsync(ExecRunHost isolated, CancellationToken ct)
    {
        var leftovers = await isolated.ListLeftoversAsync(await liveIds(ct), ct);
        foreach (var leftover in leftovers)
        {
            if (await isolated.DeleteLeftoverAsync(leftover, ct))
                logger?.LogInformation("Removed the leftover {Run}, which is finished or gone", leftover.Location);
        }
        await isolated.ReapIdleChatAsync(ct);
    }

    private static async Task<bool> WaitForNextAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
