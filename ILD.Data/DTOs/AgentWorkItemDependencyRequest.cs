namespace ILD.Data.DTOs;

/// <summary>
/// Request body for the agent-scoped add-dependency endpoint
/// (<c>POST /api/v1/agent/workitems/{id}/dependencies</c>): the item named in
/// the route waits on this one. Only the dependent item must be the caller's
/// own; this one is never edited.
/// </summary>
public class AgentWorkItemDependencyRequest
{
    public string DependsOnWorkItemId { get; set; } = string.Empty;
}
