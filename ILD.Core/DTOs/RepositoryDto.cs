namespace ILD.Core.DTOs;

public class RepositoryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CloneUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public string RemoteProviderId { get; set; } = string.Empty;
    public ILD.Core.Enums.WorkItemStatus DefaultIntakeStatus { get; set; }
    public DateTime CreatedAt { get; set; }
}
