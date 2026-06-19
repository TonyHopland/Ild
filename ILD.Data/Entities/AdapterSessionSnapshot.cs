using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

/// <summary>
/// A persisted adapter session transcript, keyed on EITHER a <see cref="LoopRun"/>
/// OR a <see cref="ChatSession"/> (see ADR-0010). Exactly one of
/// <see cref="LoopRunId"/> / <see cref="ChatSessionId"/> is set; the surrogate
/// <see cref="Id"/> primary key lets the other be null. Both owners cascade-delete
/// their snapshots, so reclaiming a run or ending a chat removes them.
/// </summary>
public class AdapterSessionSnapshot : IHasUpdatedAt
{
    [Key]
    public Guid Id { get; set; }

    [ForeignKey(nameof(LoopRun))]
    public Guid? LoopRunId { get; set; }

    [ForeignKey(nameof(ChatSession))]
    public Guid? ChatSessionId { get; set; }

    [Required]
    [MaxLength(128)]
    public string AdapterName { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string SessionId { get; set; } = string.Empty;

    [Required]
    public string SessionJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public LoopRun? LoopRun { get; set; }

    public ChatSession? ChatSession { get; set; }
}
