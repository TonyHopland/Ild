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
    public string ProviderType { get; set; } = string.Empty;

    [Required]
    [Url]
    [StringLength(2048)]
    public string BaseUrl { get; set; } = string.Empty;

    [StringLength(4096)]
    public string Token { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
