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
    public string ProviderType { get; set; } = string.Empty;

    [Required]
    [Url]
    [StringLength(512)]
    public string BaseUrl { get; set; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string DefaultModel { get; set; } = string.Empty;

    public bool IsDefault { get; set; }
    public string? Config { get; set; }
    public DateTime CreatedAt { get; set; }
}
