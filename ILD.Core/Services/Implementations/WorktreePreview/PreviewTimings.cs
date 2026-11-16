namespace ILD.Core.Services.Implementations;

/// <summary>How long a preview waits on its services. The defaults are what runs; tests shorten them.</summary>
internal sealed class PreviewTimings
{
    /// <summary>How long a stop waits for a killed service to exit.</summary>
    public TimeSpan KillGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>One second of a service's health timeout.</summary>
    public TimeSpan HealthTimeoutUnit { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Pause between health probes.</summary>
    public TimeSpan HealthPollDelay { get; set; } = TimeSpan.FromSeconds(1);
}
