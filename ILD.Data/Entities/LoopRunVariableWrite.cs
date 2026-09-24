using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

/// <summary>
/// One write to a <see cref="LoopRunVariable"/>, kept after later writes
/// overwrite the variable so a turn can still show the value it set.
/// <see cref="RunNodeId"/> is the node execution that was running when the
/// write landed; null when none was.
/// </summary>
public class LoopRunVariableWrite
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    [ForeignKey(nameof(LoopRun))]
    public Guid LoopRunId { get; set; }

    public Guid? RunNodeId { get; set; }

    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(8192)]
    public string Value { get; set; } = string.Empty;

    public DateTime WrittenAt { get; set; }

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun LoopRun { get; set; } = null!;
}
