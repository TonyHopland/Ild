using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class Repository : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [ForeignKey("RemoteProvider")]
    public Guid RemoteProviderId { get; set; }

    [Required]
    [MaxLength(2048)]
    public string CloneUrl { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? DefaultBranch { get; set; }

    [MaxLength(1024)]
    public string? WorktreesPath { get; set; }

    /// <summary>
    /// Raw text of a repository-wide custom <c>.env</c> file, injected into every
    /// preview process (install steps and services) as a baseline the committed
    /// <c>ild.config.json</c> per-service env can still override. Holds uncommitted
    /// secrets and machine-specific config, so it is encrypted at rest via
    /// <see cref="ILD.Data.Security.SecretProtector"/>. The column width and the
    /// value converter are owned by <c>AppDbContext.ConfigureSecretProtection</c>,
    /// which sizes it for the encrypted envelope (no <c>[MaxLength]</c> here, so the
    /// two can't drift); the plaintext input cap lives on <c>RepositoryDto</c>.
    /// Stored verbatim (comments/formatting preserved) and parsed at injection time.
    /// </summary>
    public string? PreviewEnv { get; set; }

    public WorkItemStatus DefaultIntakeStatus { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(RemoteProviderId))]
    public RemoteProvider RemoteProvider { get; set; } = null!;
}
