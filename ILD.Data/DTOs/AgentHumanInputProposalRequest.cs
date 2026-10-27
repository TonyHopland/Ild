using ILD.Data.Entities;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for <c>POST /api/v1/agent/workitems/{id}/human-input-proposals</c>:
/// a suggested answer to the question a work item is parked on at a Human node.
/// Suggesting delivers nothing — a human approves or rejects each suggestion.
/// </summary>
public class AgentHumanInputProposalRequest
{
    /// <summary>One of the node's outgoing edge names: OnSuccess, OnFailure or a custom edge's name.</summary>
    public string? Output { get; set; }

    /// <summary>The optional input or context passed with the output; empty is allowed.</summary>
    public string? Text { get; set; }

    /// <summary>Why the agent suggests it, shown to the human deciding.</summary>
    public string? Rationale { get; set; }
}
