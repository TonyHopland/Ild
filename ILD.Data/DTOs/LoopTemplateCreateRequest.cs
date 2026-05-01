using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class LoopTemplateCreateRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(8192)]
    public string Description { get; set; } = string.Empty;

    public List<LoopNodeDto> Nodes { get; set; } = new();

    public List<LoopNodeEdgeDto> Edges { get; set; } = new();
}
