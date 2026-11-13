namespace ILD.Core.Services.RunHosts;

/// <summary>
/// A run host with something of its own to put away when ILD stops, after the drain has parked its runs, so nothing it
/// made keeps running while ILD is down.
/// </summary>
public interface IRunHostShutdown
{
    /// <summary>Puts away what the host keeps outside ILD. Never throws: a failure is logged, and ILD stops anyway.</summary>
    Task ShutdownAsync(CancellationToken ct);
}
