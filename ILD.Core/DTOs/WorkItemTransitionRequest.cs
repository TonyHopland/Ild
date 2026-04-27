using System.ComponentModel.DataAnnotations;

namespace ILD.Core.DTOs;

public class WorkItemTransitionRequest
{
    [Required]
    public string TargetStatus { get; set; } = string.Empty;
}
