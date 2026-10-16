namespace ILD.Data.DTOs;

/// <summary>A Loop Editor save of an existing template.</summary>
public class LoopTemplateUpdateRequest : LoopTemplateCreateRequest
{
    /// <summary>
    /// The Loop Template Proposal the saved loop was reviewed from. The save
    /// marks it Applied while it can still apply, and is a plain save otherwise.
    /// </summary>
    public Guid? FromProposalId { get; set; }
}
