using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class RepositoryDto
{
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Url]
    [StringLength(2048)]
    public string CloneUrl { get; set; } = string.Empty;

    [StringLength(256)]
    public string DefaultBranch { get; set; } = "main";

    [StringLength(1024)]
    public string? WorktreesPath { get; set; }

    /// <summary>
    /// Raw text of the repository's custom <c>.env</c> file. Write-only from the
    /// client's perspective: it is accepted on create/update but never echoed back
    /// in plaintext (mirrors the provider API-key masking) — see
    /// <c>RepositoriesController</c>. Null/empty on update means "leave the stored
    /// value unchanged".
    /// </summary>
    [StringLength(16384)]
    public string? PreviewEnv { get; set; }

    [Required]
    public string RemoteProviderId { get; set; } = string.Empty;

    public ILD.Data.Enums.WorkItemStatus DefaultIntakeStatus { get; set; }
    public DateTime CreatedAt { get; set; }
}
