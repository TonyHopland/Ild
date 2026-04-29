namespace ILD.Data.DTOs;

public class LoopNodeEdgeDto
{
    public string Id { get; set; } = string.Empty;
    public string SourceNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public string EdgeType { get; set; } = string.Empty;
}
