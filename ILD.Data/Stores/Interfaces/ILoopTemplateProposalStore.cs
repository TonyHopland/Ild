using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// Loop Template Proposals (ADR-0028): agents propose a whole new document for a
/// saved loop template against its latest version, and a human saves or
/// rejects it. Saving is a new template version, written by
/// <see cref="ILoopTemplateStore.SaveProposalAsync"/>, or by
/// <see cref="ILoopTemplateStore.AddVersionAsync"/> for a Loop Editor save from it.
/// </summary>
public interface ILoopTemplateProposalStore
{
    /// <summary>
    /// Store a new pending proposal from <paramref name="proposal"/>'s source,
    /// only while its base version is the template's latest. That source's
    /// earlier pending proposal for the same template is superseded in the same
    /// transaction.
    /// </summary>
    Task<LoopTemplateProposeResult> ProposeAsync(LoopTemplateProposal proposal, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<LoopTemplateProposalEntry>> ListAsync(LoopTemplateProposalQuery query, CancellationToken ct = default);

    Task<LoopTemplateProposalEntry?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Pending proposals per template; a template with none is absent.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountPendingByTemplateAsync(CancellationToken ct = default);

    Task<LoopTemplateProposalDecision> RejectAsync(Guid id, string? reason, CancellationToken ct = default);
}

/// <summary>Every filter set narrows the list; none set lists every proposal.</summary>
public sealed record LoopTemplateProposalQuery(
    LoopTemplateProposalStatus? Status = null,
    Guid? TemplateId = null,
    Guid? CreatedByLoopRunId = null,
    Guid? CreatedByChatSessionId = null,
    string? WorkItemId = null);

public enum LoopTemplateProposeOutcome
{
    Proposed,
    TemplateNotFound,
    NotLatest,
}

/// <param name="Proposal">The stored proposal; null unless it was proposed.</param>
/// <param name="LatestVersion">The template's latest version when the proposal was refused for an older base.</param>
public sealed record LoopTemplateProposeResult(
    LoopTemplateProposeOutcome Outcome,
    LoopTemplateProposal? Proposal,
    int? LatestVersion);

/// <summary>
/// A proposal with what it is shown against: the template as it is now and its
/// latest version (null when it was deleted), the base version's graph (null
/// when it no longer exists), and its source while that still exists — the work
/// item of the run that made it, or the owner of the chat that made it.
/// </summary>
public sealed record LoopTemplateProposalEntry(
    LoopTemplateProposal Proposal,
    LoopTemplate? Template,
    int? LatestVersion,
    TemplateGraph? BaseGraph,
    string? SourceWorkItemId,
    string? SourceChatOwner);

public enum LoopTemplateProposalDecisionOutcome
{
    Rejected,
    NotPending,
    NotFound,
}

/// <param name="Entry">The proposal after the decision; null only when it was not found.</param>
/// <param name="ChangedChatSessionIds">The chats whose proposals the decision changed.</param>
public sealed record LoopTemplateProposalDecision(
    LoopTemplateProposalDecisionOutcome Outcome,
    LoopTemplateProposalEntry? Entry,
    IReadOnlyList<Guid> ChangedChatSessionIds);
