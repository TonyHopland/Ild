namespace ILD.Data.DTOs;

public class LoopNodeEdgeDto
{
    public string Id { get; set; } = string.Empty;
    public string SourceNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public string EdgeType { get; set; } = string.Empty;

    /// <summary>
    /// Custom-edge key; null for default/fallback edges. Carried through the
    /// graph so a node's named custom outlets round-trip and stay distinct.
    /// </summary>
    public string? Name { get; set; }
}
