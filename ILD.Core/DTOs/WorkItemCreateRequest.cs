using System.ComponentModel.DataAnnotations;

namespace ILD.Core.DTOs;

public class WorkItemCreateRequest
{
    [Required]
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string LoopTemplateId { get; set; } = string.Empty;

    public string RepositoryId { get; set; } = string.Empty;

    public string Priority { get; set; } = "Medium";

    public List<string> Labels { get; set; } = new();
}
