using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class WorkItemTransitionRequest
{
    [Required]
    public string TargetStatus { get; set; } = string.Empty;
}
