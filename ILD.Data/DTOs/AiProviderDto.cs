using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class AiProviderDto
{
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Type { get; set; } = string.Empty;

    [Required]
    [Url]
    [StringLength(512)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string Model { get; set; } = string.Empty;

    [StringLength(4096)]
    public string? ApiKey { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>0 = unlimited.</summary>
    [Range(0, 1000)]
    public int Parallelism { get; set; }

    public string? Config { get; set; }
    public DateTime CreatedAt { get; set; }
}
