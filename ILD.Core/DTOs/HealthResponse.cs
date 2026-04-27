namespace ILD.Core.DTOs;

public class HealthResponse
{
    public string Status { get; set; } = string.Empty;
    public HealthComponent Database { get; set; } = new();
    public HealthComponent DiskSpace { get; set; } = new();
    public HealthComponent Connectivity { get; set; } = new();
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

public class HealthComponent
{
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
}
