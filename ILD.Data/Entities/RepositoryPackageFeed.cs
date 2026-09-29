using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A repository's selection of a <see cref="PackageFeed"/>, by name. Deliberately
/// not a foreign key to the feed: deleting a feed leaves the selection in place
/// (shown as missing and skipped at run time), and a feed recreated under the
/// same name is selected again.
/// </summary>
public class RepositoryPackageFeed
{
    public Guid RepositoryId { get; set; }

    public Repository? Repository { get; set; }

    [Required]
    [MaxLength(PackageFeed.MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(PackageFeed.MaxNameLength)]
    public string NormalizedName { get; set; } = string.Empty;
}
