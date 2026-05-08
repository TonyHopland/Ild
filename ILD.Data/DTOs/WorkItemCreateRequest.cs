using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class WorkItemCreateRequest
{
    [Required]
    [StringLength(512, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [StringLength(4096)]
    public string Description { get; set; } = string.Empty;

    public string RepositoryId { get; set; } = string.Empty;

    [StringLength(32)]
    public string Priority { get; set; } = "Medium";

    /// <summary>
    /// Tags for loop-template matching and user categorisation. Sent to
    /// the WorkItemServer; the server is authoritative.
    /// </summary>
    public List<string> Tags { get; set; } = new();
}
