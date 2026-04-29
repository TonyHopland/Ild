using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

public class LoopTemplate
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Description { get; set; }

    public bool IsDefault { get; set; }

    [Required]
    [MaxLength(128)]
    public string RecoveryPolicy { get; set; } = string.Empty;

    public int MaxNodeExecutions { get; set; }

    public int MaxWallClockHours { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [InverseProperty("LoopTemplate")]
    public ICollection<LoopTemplateVersion> Versions { get; set; } = new List<LoopTemplateVersion>();
}
