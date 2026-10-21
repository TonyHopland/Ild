using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ILD.Data.Entities;

/// <summary>
/// A persisted adapter session transcript, keyed on EITHER a <see cref="LoopRun"/>
/// OR a <see cref="ChatSession"/> (see ADR-0010). Exactly one of
/// <see cref="LoopRunId"/> / <see cref="ChatSessionId"/> is set; the surrogate
/// <see cref="Id"/> primary key lets the other be null. Both owners cascade-delete
/// their snapshots, so reclaiming a run or ending a chat removes them.
///
/// The row is metadata only: the transcript's raw bytes live, compressed, in
/// <see cref="AdapterSessionSnapshotChunk"/> rows, which the entity deliberately
/// has no navigation to, so no query on snapshots can pull content in.
/// </summary>
public class AdapterSessionSnapshot : IHasUpdatedAt
{
    /// <summary>
    /// The shadow column that held the whole transcript before it moved into chunks.
    /// Only the content migration reads it; elsewhere it is only ever tested for null.
    /// Legacy (remove with migration B): the column and every use of this name.
    /// </summary>
    public const string LegacySessionJsonColumn = "SessionJson";

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

    /// <summary>What the raw content is, e.g. <c>claude-jsonl</c> (see <c>SessionSnapshotFormats</c>).</summary>
    [Required]
    [MaxLength(32)]
    public string Format { get; set; } = string.Empty;

    /// <summary>The codec every chunk's data is compressed with.</summary>
    [Required]
    [MaxLength(16)]
    public string Compression { get; set; } = string.Empty;

    /// <summary>Raw (uncompressed) bytes stored so far; an append must name it to land.</summary>
    public long ByteLength { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>Newline-terminated lines stored.</summary>
    public int EventCount { get; set; }

    /// <summary>SHA-256 of the last raw bytes stored (up to 4 KiB), to tell an appended file from a rewritten one.</summary>
    [MaxLength(64)]
    public string? TailHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public LoopRun? LoopRun { get; set; }

    public ChatSession? ChatSession { get; set; }
}
