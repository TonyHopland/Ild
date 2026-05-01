using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class RepositoryDto
{
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Url]
    [StringLength(2048)]
    public string CloneUrl { get; set; } = string.Empty;

    [StringLength(256)]
    public string DefaultBranch { get; set; } = "main";

    [Required]
    public string RemoteProviderId { get; set; } = string.Empty;

    public ILD.Data.Enums.WorkItemStatus DefaultIntakeStatus { get; set; }
    public DateTime CreatedAt { get; set; }
}
