using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for the agent-scoped (MCP) update-workitem endpoint
/// (<c>PUT /api/v1/agent/workitems/{id}</c>). An agent may only edit a work
/// item its own session created — the item's <c>CreatedByLoopRunId</c> (or
/// <c>CreatedByChatSessionId</c>) must match the caller's session, otherwise
/// the endpoint rejects the edit.
/// </summary>
public class AgentWorkItemUpdateRequest
{
    [Required]
    [StringLength(512, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Optional replacement list of tags. Omit (null) to leave tags unchanged;
    /// tags determine which loop template executes the work item — a tag must
    /// match a loop template name on the ILD instance.
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    /// Optional replacement custom branch name. Omit (null) to leave it
    /// unchanged; send an empty string to clear it back to the generated
    /// per-run name. Only the item's next run sees the change.
    /// </summary>
    [StringLength(256)]
    public string? BranchNameOverride { get; set; }
}
