namespace ILD.Data.Entities;

/// <summary>
/// One compressed piece of an <see cref="AdapterSessionSnapshot"/>'s raw content:
/// the raw bytes <c>[RawOffset, RawOffset + RawLength)</c>. Chunks are written in
/// order and never rewritten by an append; each ends right after a newline unless
/// it is the last of a write whose input did not end in one.
/// </summary>
public class AdapterSessionSnapshotChunk
{
    public Guid SnapshotId { get; set; }

    public int Seq { get; set; }

    public long RawOffset { get; set; }

    public int RawLength { get; set; }

    public byte[] Data { get; set; } = [];
}
