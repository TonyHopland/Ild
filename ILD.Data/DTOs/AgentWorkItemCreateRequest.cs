using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for the agent-scoped (MCP) create-workitem endpoint.
/// Items created via this path are always landed in Backlog regardless
/// of the repository's default intake status, and are stamped with the
/// originating LoopRun id so users can batch-clean up rogue agent output.
/// </summary>
public class AgentWorkItemCreateRequest
{
    [Required]
    [StringLength(512, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [StringLength(4096)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional LoopTemplate id (the latest version is pinned at first run start).</summary>
    public string? LoopTemplateId { get; set; }

    /// <summary>Optional Repository id.</summary>
    public string? RepositoryId { get; set; }

    /// <summary>
    /// Optional list of WorkItem ids that this item depends on. Each id must
    /// reference an existing WorkItem; cycles are rejected.
    /// </summary>
    public List<string>? Dependencies { get; set; }

    /// <summary>
    /// Optional originating LoopRun id. Falls back to the X-ILD-Run-Id request
    /// header if not provided in the body.
    /// </summary>
    public string? CreatedByLoopRunId { get; set; }

    /// <summary>
    /// Optional list of tags. Tags determine which loop template executes the
    /// work item — a tag must match a loop template name on the ILD instance.
    /// </summary>
    public List<string>? Tags { get; set; }
}
