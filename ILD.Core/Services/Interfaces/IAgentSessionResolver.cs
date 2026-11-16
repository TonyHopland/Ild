using ILD.Core.Services.Remote;
using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Who is calling the agent API, read off the caller's token and never from anything it sends.
/// A session is a run, a chat or an external-agent grant; a token names at most one.
/// </summary>
public sealed record AgentCaller(
    bool IsAgent,
    bool IsExternalAgent,
    string? SessionSource,
    Guid? SessionId,
    string? GrantScope,
    string? UserName)
{
    public Guid? RunId => SessionClaim(AgentTokenSource.Run);

    public Guid? ChatSessionId => SessionClaim(AgentTokenSource.Chat);

    public Guid? GrantId => SessionClaim(AgentTokenSource.Grant);

    public bool HasControlGrant => IsExternalAgent && GrantScope == nameof(ExternalAgentGrantScope.Control);

    /// <summary>The id the token names for <paramref name="source"/>, or null when its session is of another kind.</summary>
    public Guid? SessionClaim(AgentTokenSource source)
        => (IsAgent || IsExternalAgent) && SessionSource == source.ToString().ToLowerInvariant() ? SessionId : null;
}

/// <summary>The session a proposal comes from: the run, the chat or the grant, never more than one.</summary>
public sealed record AgentProposer(Guid? RunId, Guid? ChatSessionId, Guid? GrantId);

public enum ProposerOutcome
{
    Resolved,

    /// <summary>The token names a session that does not exist.</summary>
    Unknown,

    /// <summary>The caller has no session to propose as.</summary>
    NoSession,
}

public sealed record ProposerResolution(ProposerOutcome Outcome, AgentProposer? Proposer, string? Error);

/// <summary>
/// What every proposal carries about where it came from: its session, the step the run was at and the chat reply it follows.
/// </summary>
public sealed record ProposalStamp(
    Guid? RunId, Guid? ChatSessionId, Guid? GrantId, Guid? RunNodeId, int? ChatReplySequence)
{
    public void ApplyTo(GlobalVariableProposal proposal)
    {
        proposal.CreatedByLoopRunId = RunId;
        proposal.CreatedByChatSessionId = ChatSessionId;
        proposal.CreatedByExternalGrantId = GrantId;
        proposal.CreatedByRunNodeId = RunNodeId;
        proposal.ChatReplySequence = ChatReplySequence;
    }

    public void ApplyTo(LoopTemplateProposal proposal)
    {
        proposal.CreatedByLoopRunId = RunId;
        proposal.CreatedByChatSessionId = ChatSessionId;
        proposal.CreatedByExternalGrantId = GrantId;
        proposal.CreatedByRunNodeId = RunNodeId;
        proposal.ChatReplySequence = ChatReplySequence;
    }

    public void ApplyTo(RemoteCreateEditProposalRequest request)
    {
        request.CreatedByLoopRunId = RunId;
        request.CreatedByChatSessionId = ChatSessionId;
        request.CreatedByExternalGrantId = GrantId;
        request.CreatedByRunNodeId = RunNodeId;
        request.ChatReplySequence = ChatReplySequence;
    }
}

/// <summary>
/// Who the caller of the agent API is, what it owns and what it may reach.
/// </summary>
public interface IAgentSessionResolver
{
    /// <summary>
    /// Who a change is for: a loop run as this instance's user, named by the step it is running and tied to the work
    /// item it works on; else a chat session as the user it belongs to; else an external-agent grant; else the
    /// signed-in person, or this instance for an agent token that names no session. Null for a signed-in session
    /// whose user can no longer be resolved.
    /// </summary>
    Task<WorkItemActor?> ActorAsync(AgentCaller caller);

    /// <summary>
    /// True iff the work item was created by the caller's own session: the run, the chat or the grant its token names.
    /// </summary>
    bool Owns(AgentCaller caller, WorkItemView workItem);

    /// <summary>
    /// Null when the caller may act on the work item's worktree, preview, pull request, conversation or attachments,
    /// else why not. A run reaches only its own run's work item and the items that run created, a grant only the items
    /// it created; a chat, driven by the operator, and a user reach any. An unknown item passes, for the route to answer 404.
    /// </summary>
    Task<string?> ReachRefusalAsync(AgentCaller caller, string workItemId);

    /// <summary>
    /// The session a proposal is stamped with, required to exist: the decision is fed back to that session, so a
    /// proposal from nowhere would have nowhere to go. The run wins over a chat, and a chat over a grant.
    /// </summary>
    Task<ProposerResolution> ResolveProposerAsync(AgentCaller caller);

    Task<ProposalStamp> StampAsync(AgentProposer proposer);

    Task<bool> RunExistsAsync(Guid runId);

    Task<bool> ChatSessionExistsAsync(Guid chatSessionId);

    /// <summary>Whether the run is the one working on the work item.</summary>
    Task<bool> RunWorksOnAsync(Guid runId, string workItemId);
}
