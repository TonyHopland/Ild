using System.ComponentModel.DataAnnotations;
using ILD.Data.Enums;

namespace ILD.Data.DTOs;

public class LoopTemplateCreateRequest
{
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(8192)]
    public string Description { get; set; } = string.Empty;

    public RecoveryPolicy RecoveryPolicy { get; set; } = RecoveryPolicy.AutoResume;

    public int MaxNodeExecutions { get; set; } = 200;

    public List<LoopNodeDto> Nodes { get; set; } = new();

    public List<LoopNodeEdgeDto> Edges { get; set; } = new();
}
