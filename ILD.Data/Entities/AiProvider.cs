using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

public class AiProvider : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Type { get; set; } = string.Empty;

    [Required]
    [MaxLength(1024)]
    public string BaseUrl { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? ApiKey { get; set; }

    [Required]
    [MaxLength(256)]
    public string Model { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public string? Config { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
