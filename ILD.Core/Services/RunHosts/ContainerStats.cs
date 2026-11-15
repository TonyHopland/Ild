namespace ILD.Core.Services.RunHosts;

/// <summary>
/// What a container's engine says a run is using. Every figure is null when the engine does not expose it; a limit is null when
/// the container has none. <see cref="Running"/> is false for a container that is stopped or gone, and then nothing else is set.
/// </summary>
public sealed record ContainerStats(
    bool Running,
    double? CpuCores = null,
    double? CpuLimitCores = null,
    long? MemoryBytes = null,
    long? MemoryLimitBytes = null,
    long? NetworkRxBytes = null,
    long? NetworkTxBytes = null,
    long? DiskReadBytes = null,
    long? DiskWriteBytes = null)
{
    public static ContainerStats NotRunning { get; } = new(false);
}
