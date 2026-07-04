using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

public class WorkItemCreateRequest
{
    [Required]
    [StringLength(512, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string RepositoryId { get; set; } = string.Empty;

    [StringLength(32)]
    public string Priority { get; set; } = "Medium";

    /// <summary>
    /// Tags for loop-template matching and user categorisation. Sent to
    /// the WorkItemServer; the server is authoritative.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// AI provider override mode as its enum name ("None", "OverrideDefault",
    /// "OverrideAll"). Null leaves the existing override unchanged on update.
    /// </summary>
    [StringLength(32)]
    public string? AiProviderOverride { get; set; }

    /// <summary>
    /// The AI provider id an override targets (empty/null = no target). Only
    /// applied when <see cref="AiProviderOverride"/> is supplied.
    /// </summary>
    public string? AiProviderOverrideId { get; set; }
}
