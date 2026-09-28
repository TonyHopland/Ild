using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A private package feed (Azure Artifacts) whose read-only PAT ILD hands to the
/// package managers of the runs whose repository selected it. The name is the
/// feed's identity: repositories select feeds by name, so it cannot change after
/// creation and is unique ignoring case (<see cref="NormalizedName"/>).
/// </summary>
public class PackageFeed
{
    public const int MaxNameLength = 128;

    /// <summary>Plaintext cap, well inside the encrypted column.</summary>
    public const int MaxPatLength = 1024;

    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxNameLength)]
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>The canonical feed URL, without a trailing slash.</summary>
    [Required]
    [MaxLength(512)]
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted at rest; the column width and value converter are owned by
    /// <c>AppDbContext.ConfigureSecretProtection</c>. Never leaves the API.
    /// </summary>
    public string Pat { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
