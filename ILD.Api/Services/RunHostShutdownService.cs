using ILD.Core.Services.RunHosts;

namespace ILD.Api.Services;

/// <summary>
/// When ILD stops, lets the run host put away the containers or pods it keeps (<see cref="IRunHostShutdown"/>). Hosted
/// services stop in the reverse of their registration, and this one is registered before
/// <see cref="GracefulRunDrainService"/>, so it runs once the drain has parked the runs and killed their agents.
/// </summary>
public sealed class RunHostShutdownService(IRunHost host, ILogger<RunHostShutdownService> log) : IHostedService
{
    /// <summary>How long it may take; what is left over after the drain inside the host's shutdown timeout.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (host is not RoutingRunHost { Isolated: IRunHostShutdown shutdown }) return;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Budget);
        try
        {
            await shutdown.ShutdownAsync(limit.Token);
            log.LogInformation("Put away the run host's containers for the shutdown");
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("Putting away the run host's containers took longer than {Seconds} s; what is left is handled when ILD next starts", Budget.TotalSeconds);
        }
    }
}
