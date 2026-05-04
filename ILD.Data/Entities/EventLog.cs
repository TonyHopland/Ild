using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class EventLog
{
    [Key]
    public Guid Id { get; set; }

    [ForeignKey("LoopRun")]
    public Guid? LoopRunId { get; set; }

    public int Sequence { get; set; }

    public EventType EventType { get; set; }

    public Guid? NodeId { get; set; }

    public Guid? RunNodeId { get; set; }

    public DateTime Timestamp { get; set; }

    [MaxLength(1024)]
    public string? PayloadPath { get; set; }

    public string? Data { get; set; }

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun? LoopRun { get; set; }
}
