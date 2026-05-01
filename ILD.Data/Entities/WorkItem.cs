using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class WorkItem : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4096)]
    public string? Description { get; set; }

    public WorkItemPriority Priority { get; set; }

    public WorkItemStatus Status { get; set; }

    [Required]
    [ForeignKey("Repository")]
    public Guid RepositoryId { get; set; }

    [ForeignKey("LoopTemplateVersion")]
    public Guid? LoopTemplateVersionId { get; set; }

    [ForeignKey("CurrentLoopRun")]
    public Guid? CurrentLoopRunId { get; set; }

    public string? Labels { get; set; }

    [MaxLength(1024)]
    public string? WorktreePath { get; set; }

    [MaxLength(256)]
    public string? BranchName { get; set; }

    [MaxLength(2048)]
    public string? PrUrl { get; set; }

    public bool IsPrMerged { get; set; }

    [MaxLength(512)]
    public string? HumanFeedbackReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public Repository Repository { get; set; } = null!;

    [ForeignKey(nameof(LoopTemplateVersionId))]
    public LoopTemplateVersion? LoopTemplateVersion { get; set; }

    [ForeignKey(nameof(CurrentLoopRunId))]
    public LoopRun? CurrentLoopRun { get; set; }

    [InverseProperty("WorkItem")]
    public ICollection<LoopRun> LoopRuns { get; set; } = new List<LoopRun>();

    [InverseProperty("WorkItem")]
    public ICollection<WorkItemDependency> Dependencies { get; set; } = new List<WorkItemDependency>();

    [InverseProperty("DependentWorkItem")]
    public ICollection<WorkItemDependency> DependentDependencies { get; set; } = new List<WorkItemDependency>();
}
