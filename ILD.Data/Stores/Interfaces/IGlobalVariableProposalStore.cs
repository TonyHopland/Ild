using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// Global Variable Proposals (ADR-0027): agents suggest a whole new value for a
/// global variable, and a human approves or rejects it. Approving applies the
/// value only while the variable is as the suggestion found it.
/// </summary>
public interface IGlobalVariableProposalStore
{
    /// <summary>
    /// Store a new pending suggestion from <paramref name="proposal"/>'s source,
    /// snapshotting the variable it names. That source's earlier pending
    /// suggestion for the same variable is superseded in the same transaction.
    /// </summary>
    Task<GlobalVariableProposal> ProposeAsync(GlobalVariableProposal proposal, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<GlobalVariableProposalEntry>> ListAsync(GlobalVariableProposalQuery query, CancellationToken ct = default);

    Task<GlobalVariableProposalEntry?> GetAsync(Guid id, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Write the suggested value if the variable still matches the snapshot,
    /// making every other pending suggestion for it stale; otherwise the
    /// suggestion itself goes stale and nothing is written.
    /// </summary>
    Task<GlobalVariableProposalDecision> ApproveAsync(Guid id, CancellationToken ct = default);

    Task<GlobalVariableProposalDecision> RejectAsync(Guid id, string? reason, CancellationToken ct = default);
}

/// <summary>Every filter set narrows the list; none set lists every suggestion.</summary>
public sealed record GlobalVariableProposalQuery(
    GlobalVariableProposalStatus? Status = null,
    Guid? CreatedByLoopRunId = null,
    Guid? CreatedByChatSessionId = null,
    string? WorkItemId = null);

/// <summary>
/// A suggestion with what it is shown against: the variable as it is now (null
/// when there is none), and its source while that still exists — the work item
/// of the run that made it, or the owner of the chat that made it.
/// </summary>
public sealed record GlobalVariableProposalEntry(
    GlobalVariableProposal Proposal,
    GlobalVariable? Variable,
    string? SourceWorkItemId,
    string? SourceChatOwner);

public enum GlobalVariableProposalDecisionOutcome
{
    Approved,
    Rejected,
    Stale,
    NotPending,
    NotFound,
}

/// <param name="Entry">The suggestion after the decision; null only when it was not found.</param>
/// <param name="ChangedChatSessionIds">The chats whose suggestions the decision changed.</param>
public sealed record GlobalVariableProposalDecision(
    GlobalVariableProposalDecisionOutcome Outcome,
    GlobalVariableProposalEntry? Entry,
    IReadOnlyList<Guid> ChangedChatSessionIds);
