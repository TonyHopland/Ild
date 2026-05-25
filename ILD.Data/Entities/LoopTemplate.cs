using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class LoopTemplate : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Description { get; set; }

    public bool IsDefault { get; set; }

    public bool IsArchived { get; set; }

    [Required]
    [MaxLength(128)]
    public RecoveryPolicy RecoveryPolicy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [InverseProperty("LoopTemplate")]
    public ICollection<LoopTemplateVersion> Versions { get; set; } = new List<LoopTemplateVersion>();
}
