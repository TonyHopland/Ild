namespace ILD.Data.DTOs;

public class LoopTemplateDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Version { get; set; }
    public List<LoopNodeDto> Nodes { get; set; } = new();
    public List<LoopNodeEdgeDto> Edges { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
