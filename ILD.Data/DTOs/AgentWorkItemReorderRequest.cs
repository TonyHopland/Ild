namespace ILD.Data.DTOs;

/// <summary>
/// Where a work item goes, named by a neighbour: directly after <see cref="AboveId"/> or directly before
/// <see cref="BelowId"/>. At least one is required. The item keeps its status.
/// </summary>
public class AgentWorkItemReorderRequest
{
    public string? AboveId { get; set; }

    public string? BelowId { get; set; }
}
