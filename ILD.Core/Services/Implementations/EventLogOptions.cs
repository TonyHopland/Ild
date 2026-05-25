namespace ILD.Core.Services.Implementations;

public class EventLogOptions
{
    public string PayloadDirectory { get; set; } = "data/payloads";
    public int LargePayloadThresholdBytes { get; set; } = 10_240;

    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromDays(1);
}
