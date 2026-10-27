using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The rules of Human Input Proposals (ADR-0030): what may be suggested for a
/// work item parked at a Human node, and what approving or rejecting a
/// suggestion does. The decider is a plain name, so a person's click and a
/// later automatic approver go through the same calls.
/// </summary>
public interface IHumanInputProposalService
{
    /// <summary>
    /// Store a suggestion from the given run or chat for the work item, if it is
    /// parked for human input and <paramref name="output"/> is one of the
    /// parked node's outputs.
    /// </summary>
    Task<HumanInputProposalResult> ProposeAsync(
        string workItemId, string output, string text, string rationale,
        Guid? runId, Guid? chatSessionId, CancellationToken ct);

    /// <summary>
    /// Deliver a pending suggestion's output and text to the visit it was made
    /// for, as the matching Action-tab button would. When that visit is no
    /// longer open nothing is delivered and the suggestion goes stale.
    /// </summary>
    Task<HumanInputProposalDecision> ApproveAsync(Guid id, string decidedBy, CancellationToken ct);

    Task<HumanInputProposalDecision> RejectAsync(Guid id, string? reason, string decidedBy, CancellationToken ct);
}

public enum HumanInputProposalOutcome
{
    Created,
    NotFound,

    /// <summary>The work item is not waiting for human input at a Human node.</summary>
    NotParked,

    OutputNotOffered,
}

/// <param name="OfferedOutputs">The outputs the parked node offers; set when the output was not one of them.</param>
public sealed record HumanInputProposalResult(
    HumanInputProposalOutcome Outcome,
    HumanInputProposal? Proposal = null,
    IReadOnlyList<string>? OfferedOutputs = null);

public enum HumanInputDecisionOutcome
{
    Approved,
    Rejected,

    /// <summary>The visit was no longer open: nothing was delivered.</summary>
    Stale,

    /// <summary>Already decided, superseded or made stale, or decided by someone else a moment earlier.</summary>
    NotPending,

    NotFound,
}

/// <param name="Proposal">The suggestion after the decision; null only when it was not found.</param>
public sealed record HumanInputProposalDecision(HumanInputDecisionOutcome Outcome, HumanInputProposal? Proposal);
