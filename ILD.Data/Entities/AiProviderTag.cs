using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A user-chosen label on an AI provider (QA, Fast, Thinking…) that an AI node
/// names to run on that provider. A tag has at most one holder: the unique
/// index on <see cref="NormalizedName"/> makes "qa" and "QA" the same tag
/// across every provider.
/// </summary>
public class AiProviderTag
{
    public const int MaxNameLength = 64;
    public const int MaxPerProvider = 32;

    [Key]
    public Guid Id { get; set; }

    public Guid AiProviderId { get; set; }

    public AiProvider? AiProvider { get; set; }

    /// <summary>The spelling the user last saved it with.</summary>
    [Required]
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxNameLength)]
    public string NormalizedName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    /// <summary>
    /// Why <paramref name="name"/> (already trimmed) cannot be a tag, or null
    /// when it can. Commas are out because tags are typed as a comma-separated
    /// list.
    /// </summary>
    public static string? Problem(string name)
    {
        if (name.Length > MaxNameLength) return $"is longer than {MaxNameLength} characters";
        if (name.Contains(',')) return "contains a comma";
        return null;
    }
}
