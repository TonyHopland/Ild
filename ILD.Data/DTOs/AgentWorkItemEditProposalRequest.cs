using System.ComponentModel.DataAnnotations;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for the agent-scoped propose-edit endpoint
/// (<c>POST /api/v1/agent/workitems/{id}/edit-proposals</c>). Every field is
/// optional and only the ones sent are proposed: a field left out (or null) is
/// not a change, and an empty branch override is a proposal to clear it. At
/// least one of the five fields must be sent. Proposing applies nothing — a
/// human approves or rejects each proposal.
/// </summary>
public class AgentWorkItemEditProposalRequest
{
    [StringLength(512)]
    public string? Title { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Replacement list of tags. Not checked against loop template names: loop
    /// logic reads other tags too (HIL, for one).
    /// </summary>
    public List<string>? Tags { get; set; }

    [StringLength(256)]
    public string? BranchNameOverride { get; set; }

    [StringLength(256)]
    public string? BaseBranchOverride { get; set; }

    /// <summary>Why the agent proposes it, shown to the human deciding.</summary>
    [StringLength(2000)]
    public string? Rationale { get; set; }
}
