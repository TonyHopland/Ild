using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class RemoteProviderDto
{
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(64)]
    public string Type { get; set; } = string.Empty;

    [Required]
    [Url]
    [StringLength(2048)]
    public string BaseUrl { get; set; } = string.Empty;

    [StringLength(4096)]
    public string? ApiKey { get; set; }

    [StringLength(512)]
    public string? WebhookSecret { get; set; }

    public bool IsDefault { get; set; }

    [Url]
    [StringLength(1024)]
    public string? WorkItemServerUrl { get; set; }

    [StringLength(1024)]
    public string? WorkItemApiKey { get; set; }

    [Range(1, 86400)]
    public int PollIntervalSeconds { get; set; } = 60;

    [Range(1, 3600)]
    public int GraceIntervalSeconds { get; set; } = 5;

    [Range(1, 100)]
    public int MaxConcurrentWorkItems { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
}
