using System.ComponentModel.DataAnnotations;
using ILD.Data.Entities;

namespace ILD.Data.DTOs;

/// <summary>
/// Request body for <c>POST /api/v1/agent/loop-templates/proposals</c>: a whole
/// new <c>ild-loop-template/v2</c> document for a saved loop template, or an
/// Edit List to make one from, against the version the agent read. Proposing saves nothing — a human saves
/// or rejects each proposal.
/// </summary>
public class AgentLoopTemplateProposalRequest
{
    /// <summary>The template's id, or else its exact name, as get_loop_template takes it.</summary>
    public string? NameOrId { get; set; }

    /// <summary>The version the document was read from; it must be the latest.</summary>
    public int? BaseVersion { get; set; }

    /// <summary>The complete proposed document, as JSON text. Exclusive with <see cref="Edits"/>.</summary>
    public string? Document { get; set; }

    /// <summary>An Edit List to apply to the latest version instead of a whole document.</summary>
    public List<LoopTemplateEdit?>? Edits { get; set; }

    /// <summary>Why the agent proposes it, shown to the human deciding.</summary>
    [StringLength(LoopTemplateProposal.MaxRationaleLength)]
    public string? Rationale { get; set; }
}
