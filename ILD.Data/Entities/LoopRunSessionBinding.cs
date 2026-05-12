using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

public class LoopRunSessionBinding : IHasUpdatedAt
{
    [Required]
    [ForeignKey(nameof(LoopRun))]
    public Guid LoopRunId { get; set; }

    [Required]
    [MaxLength(128)]
    public string AdapterName { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string PlaceholderId { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string SessionId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun LoopRun { get; set; } = null!;
}