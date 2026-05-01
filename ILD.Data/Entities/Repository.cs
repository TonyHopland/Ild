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

    public WorkItemStatus DefaultIntakeStatus { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(RemoteProviderId))]
    public RemoteProvider RemoteProvider { get; set; } = null!;

    [InverseProperty("Repository")]
    public ICollection<WorkItem> WorkItems { get; set; } = new List<WorkItem>();
}
