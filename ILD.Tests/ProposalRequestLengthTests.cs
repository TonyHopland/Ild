using System.ComponentModel.DataAnnotations;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>The length limits the proposal request bodies declare, which the host checks as model state.</summary>
public sealed class ProposalRequestLengthTests
{
    private static List<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void A_work_item_edit_proposal_over_the_length_limits_fails_validation()
    {
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.Title),
            Validate(new AgentWorkItemEditProposalRequest { Title = new string('t', 513) }).SelectMany(r => r.MemberNames));
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.BranchNameOverride),
            Validate(new AgentWorkItemEditProposalRequest { BranchNameOverride = new string('b', 257) }).SelectMany(r => r.MemberNames));
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.BaseBranchOverride),
            Validate(new AgentWorkItemEditProposalRequest { BaseBranchOverride = new string('b', 257) }).SelectMany(r => r.MemberNames));
        Assert.Contains(nameof(AgentWorkItemEditProposalRequest.Rationale),
            Validate(new AgentWorkItemEditProposalRequest { Title = "ok", Rationale = new string('r', 2001) }).SelectMany(r => r.MemberNames));
        Assert.Empty(Validate(new AgentWorkItemEditProposalRequest
        {
            Title = new string('t', 512),
            BranchNameOverride = new string('b', 256),
            BaseBranchOverride = new string('b', 256),
            Rationale = new string('r', 2000),
        }));
    }

    [Fact]
    public void A_rationale_over_2000_fails_validation_on_the_loop_and_variable_requests_and_2000_passes()
    {
        Assert.Contains(nameof(AgentLoopTemplateProposalRequest.Rationale),
            Validate(new AgentLoopTemplateProposalRequest { Rationale = new string('r', 2001) }).SelectMany(r => r.MemberNames));
        Assert.Contains(nameof(AgentGlobalVariableProposalRequest.Rationale),
            Validate(new AgentGlobalVariableProposalRequest { Rationale = new string('r', 2001) }).SelectMany(r => r.MemberNames));
        Assert.Empty(Validate(new AgentLoopTemplateProposalRequest { Rationale = new string('r', 2000) }));
        Assert.Empty(Validate(new AgentGlobalVariableProposalRequest { Rationale = new string('r', 2000) }));
    }
}
