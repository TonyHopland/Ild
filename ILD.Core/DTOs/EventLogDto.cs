namespace ILD.Core.DTOs;

public class EventLogDto
{
    public long Sequence { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? NodeId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
