using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

/// <summary>
/// A named, mutable string value scoped to a single <see cref="LoopRun"/>.
/// A Set Variable node writes these, and AI nodes read and write them via the
/// agent API, so one node can hand off state to a later node — e.g. an AI writes
/// a handoff summary that a downstream AI or the PR node consumes via the
/// <c>{{Var.&lt;name&gt;}}</c> placeholder.
///
/// Keyed by (<see cref="LoopRunId"/>, <see cref="Name"/>): a variable belongs to
/// exactly one run and a run holds at most one value per name. Cascade-deleted
/// with the run, mirroring <see cref="LoopRunSessionBinding"/>.
/// </summary>
public class LoopRunVariable : IHasUpdatedAt
{
    public const int MaxValueLength = 8192;

    [Required]
    [ForeignKey(nameof(LoopRun))]
    public Guid LoopRunId { get; set; }

    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(MaxValueLength)]
    public string Value { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun LoopRun { get; set; } = null!;
}
