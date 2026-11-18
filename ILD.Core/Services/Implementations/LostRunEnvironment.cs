using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Enums;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Makes a run's lost pod or container again before something is run in it, and records on the run what of its work came back (ADR-0040).
/// Never throws for a host that cannot: the command that follows then fails with <see cref="RunPodLostException"/>, which says what to do.
/// </summary>
internal static class LostRunEnvironment
{
    public static async Task RestoreAsync(IRunLifecycle? host, IEventLogService? events, ILogger logger, LoopRun run, Guid? nodeId, CancellationToken ct)
    {
        if (host is null || string.IsNullOrEmpty(run.WorktreePath)) return;
        try
        {
            if (await host.RecoverAsync(run.Id, ct) is not { } recovered) return;
            logger.LogWarning("Run {RunId}: {Recovered}", run.Id, recovered);
            if (events is not null) await events.AppendAsync(run.Id, EventType.RunRecovered, recovered, nodeId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Run {RunId}: its lost environment could not be made again", run.Id);
        }
    }
}
