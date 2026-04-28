namespace ILD.Core.Services.Implementations;

public class EventLogOptions
{
    public string PayloadDirectory { get; set; } = "data/payloads";
    public int LargePayloadThresholdBytes { get; set; } = 10_240;
}
