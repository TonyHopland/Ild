using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The rules of Global Variable Proposals (ADR-0027) as an agent makes them: what may be suggested for a global
/// variable and who hears of it. A human decides a suggestion elsewhere.
/// </summary>
public interface IGlobalVariableProposalService
{
    Task<GlobalVariableProposalResult> ProposeAsync(
        AgentGlobalVariableProposalRequest request, AgentProposer proposer, CancellationToken ct);
}

public enum GlobalVariableProposalOutcome
{
    Created,

    /// <summary>The suggestion breaks a rule; the result's error says which.</summary>
    Invalid,
}

public sealed record GlobalVariableProposalResult(
    GlobalVariableProposalOutcome Outcome, GlobalVariableProposal? Proposal = null, string? Error = null);
