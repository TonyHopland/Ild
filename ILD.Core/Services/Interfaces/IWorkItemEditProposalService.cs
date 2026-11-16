using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// The rules of Work Item Edit Proposals (ADR-0022) as an agent makes them: what is checked before the WorkItem
/// server is asked, so a proposal that could never be applied is refused now rather than when a human approves it.
/// A human decides a proposal elsewhere. An unreachable WorkItem server surfaces as <see cref="HttpRequestException"/>.
/// </summary>
public interface IWorkItemEditProposalService
{
    Task<WorkItemEditProposalResult> ProposeAsync(
        string workItemId, AgentWorkItemEditProposalRequest request, AgentCaller caller, AgentProposer proposer, CancellationToken ct);
}

public enum WorkItemEditProposalOutcome
{
    Created,

    /// <summary>The proposal breaks a rule; the result's error says which.</summary>
    Invalid,

    NotFound,

    TooManyPending,

    /// <summary>The caller's user can no longer be resolved.</summary>
    Unauthorized,
}

public sealed record WorkItemEditProposalResult(
    WorkItemEditProposalOutcome Outcome, string? Error = null, RemoteWorkItemEditProposal? Proposal = null);
