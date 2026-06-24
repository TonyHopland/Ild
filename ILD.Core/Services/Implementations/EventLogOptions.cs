namespace ILD.Core.Services.Implementations;

public class EventLogOptions
{
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromDays(1);
}
