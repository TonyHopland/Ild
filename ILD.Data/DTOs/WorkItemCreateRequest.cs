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

    /// <summary>
    /// Custom branch name for every run of this work item, used verbatim with no
    /// per-run suffix. Null leaves it unchanged on update; an empty string
    /// clears it back to the generated per-run name.
    /// </summary>
    [StringLength(256)]
    public string? BranchNameOverride { get; set; }

    /// <summary>
    /// Ref every run of this work item branches from — fetched, reset to, and
    /// rebased onto, and the branch its PR targets. Null leaves it unchanged on
    /// update; an empty string clears it back to the repository's default branch.
    /// </summary>
    [StringLength(256)]
    public string? BaseBranchOverride { get; set; }
}
