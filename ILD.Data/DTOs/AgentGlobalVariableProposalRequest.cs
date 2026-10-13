using System.ComponentModel.DataAnnotations;
using ILD.Data.Entities;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for <c>POST /api/v1/agent/global-variables/proposals</c>: a
/// suggested whole new value for a global variable, which a name no variable
/// has suggests creating. Suggesting applies nothing — a human approves or
/// rejects each suggestion.
/// </summary>
public class AgentGlobalVariableProposalRequest
{
    public string? Name { get; set; }

    /// <summary>The complete new text; empty is a value too.</summary>
    public string? Value { get; set; }

    /// <summary>Why the agent suggests it, shown to the human deciding.</summary>
    [StringLength(GlobalVariableProposal.MaxRationaleLength)]
    public string? Rationale { get; set; }
}
