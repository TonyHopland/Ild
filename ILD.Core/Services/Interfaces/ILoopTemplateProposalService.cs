using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The rules of Loop Template Proposals (ADR-0028) as an agent makes them: a whole document, or an Edit List
/// applied to the latest version, checked as a save would check it. A human saves or rejects one elsewhere.
/// </summary>
public interface ILoopTemplateProposalService
{
    Task<LoopTemplateProposalResult> ProposeAsync(
        AgentLoopTemplateProposalRequest request, AgentProposer proposer, CancellationToken ct);
}

public enum LoopTemplateProposalOutcome
{
    Created,

    /// <summary>The proposal breaks a rule; the result's error says which.</summary>
    Invalid,

    TemplateNotFound,

    /// <summary>More than one template has the name; the result lists them.</summary>
    AmbiguousName,

    /// <summary>The base version is not the template's latest; the result names the latest.</summary>
    NotLatest,
}

/// <param name="EditIndex">The Edit List entry that failed to apply, when that is why it was refused.</param>
/// <param name="ValidationErrors">Every rule the proposed graph breaks, when that is why it was refused.</param>
/// <param name="Candidates">The templates sharing the name; set when it was ambiguous.</param>
/// <param name="LatestVersion">The template's latest version; set when the base version was not it.</param>
public sealed record LoopTemplateProposalResult(
    LoopTemplateProposalOutcome Outcome,
    LoopTemplateProposal? Proposal = null,
    string? Error = null,
    int? EditIndex = null,
    IReadOnlyList<string>? ValidationErrors = null,
    IReadOnlyList<LoopTemplate>? Candidates = null,
    int? LatestVersion = null);
