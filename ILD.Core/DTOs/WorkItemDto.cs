namespace ILD.Core.DTOs;

public class WorkItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public List<string> Labels { get; set; } = new();
    public string LoopTemplateId { get; set; } = string.Empty;
    public string LoopTemplateVersion { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string? PullRequestUrl { get; set; }
    public string? PullRequestBranch { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<string> DependencyIds { get; set; } = new();
    public List<string> DependentIds { get; set; } = new();
}
