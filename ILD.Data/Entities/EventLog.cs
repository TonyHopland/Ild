using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ILD.Data.Enums;

namespace ILD.Data.Entities;

public class EventLog
{
    /// <summary>
    /// Database identity. Within a run it is the event's position in the
    /// timeline: appends commit in Id order (see <c>EventLogStore.AppendAsync</c>),
    /// so a reader paging by Id never skips an event that commits later.
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [ForeignKey("LoopRun")]
    public Guid? LoopRunId { get; set; }

    public EventType EventType { get; set; }

    public Guid? NodeId { get; set; }

    public Guid? RunNodeId { get; set; }

    /// <summary>The named edge a human chose, when the event records that choice.</summary>
    [MaxLength(256)]
    public string? EdgeName { get; set; }

    public DateTime Timestamp { get; set; }

    [MaxLength(1024)]
    public string? PayloadPath { get; set; }

    public string? Data { get; set; }

    [ForeignKey(nameof(LoopRunId))]
    public LoopRun? LoopRun { get; set; }
}
