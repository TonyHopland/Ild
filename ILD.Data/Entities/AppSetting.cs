using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

public class AppSetting : IHasUpdatedAt
{
    [Key]
    public const int MaxValueLength = 4096;

    public Guid Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxValueLength)]
    public string Value { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }
}
