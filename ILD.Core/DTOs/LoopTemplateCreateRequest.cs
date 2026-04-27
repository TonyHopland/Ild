using System.ComponentModel.DataAnnotations;

namespace ILD.Core.DTOs;

public class LoopTemplateCreateRequest
{
    [Required]
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<LoopNodeDto> Nodes { get; set; } = new();

    public List<LoopNodeEdgeDto> Edges { get; set; } = new();
}
