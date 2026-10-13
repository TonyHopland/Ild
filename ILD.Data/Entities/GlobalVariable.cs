using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// An instance-wide named value loop prompts read with <c>{{Global.&lt;name&gt;}}</c>.
/// The name is the variable's identity: templates reference it by name, so it
/// cannot change after creation and is unique ignoring case (<see cref="NormalizedName"/>).
/// There is no history: an edit overwrites the value.
/// </summary>
public class GlobalVariable
{
    public const int MaxNameLength = 128;

    /// <summary>The column width; the limit a value must fit is the <c>globalVariables.maxLength</c> setting.</summary>
    public const int MaxValueLength = 8192;

    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxNameLength)]
    public string NormalizedName { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxValueLength)]
    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
