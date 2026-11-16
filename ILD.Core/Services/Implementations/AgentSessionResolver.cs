using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public sealed class AgentSessionResolver : IAgentSessionResolver
{
    private readonly AppDbContext _db;
    private readonly ILoopRunStore _runs;
    private readonly IWorkItemManager _workItems;
    private readonly BootstrapCredentials _instance;

    public AgentSessionResolver(AppDbContext db, ILoopRunStore runs, IWorkItemManager workItems, BootstrapCredentials instance)
    {
        _db = db;
        _runs = runs;
        _workItems = workItems;
        _instance = instance;
    }

    public async Task<WorkItemActor?> ActorAsync(AgentCaller caller)
    {
        if (caller.RunId is { } run && await _runs.GetByIdAsync(run) is { } loopRun)
        {
            var step = await _runs.GetRunningNodeIdAsync(run) is { } runNodeId
                ? (await _runs.GetRunNodeByIdAsync(runNodeId))?.NodeLabel
                : null;
            return WorkItemActor.Agent(_instance.Username, run, step, loopRun.WorkItemId);
        }
        if (caller.ChatSessionId is { } chat
            && await _db.ChatSessions.AsNoTracking().Where(c => c.Id == chat).Select(c => c.UserId).FirstOrDefaultAsync() is { } chatUser)
            return WorkItemActor.Chat(chatUser, chat);
        if (caller.GrantId is { } grant
            && await _db.ExternalAgentGrants.AsNoTracking().Where(g => g.Id == grant).Select(g => g.Name).FirstOrDefaultAsync() is { } grantName)
            return WorkItemActor.External(_instance.Username, grantName, grant);
        if (caller.IsAgent)
            return WorkItemActor.System(_instance.Username, "agent with no run or chat");
        return caller.UserName is { Length: > 0 } name ? WorkItemActor.Human(name) : null;
    }

    public bool Owns(AgentCaller caller, WorkItemView workItem)
    {
        if (caller.RunId is { } runId)
            return workItem.CreatedByLoopRunId == runId;
        if (caller.ChatSessionId is { } chatId)
            return workItem.CreatedByChatSessionId == chatId;
        if (caller.GrantId is { } grantId)
            return workItem.CreatedByExternalGrantId == grantId;
        return false;
    }

    public async Task<string?> ReachRefusalAsync(AgentCaller caller, string workItemId)
    {
        if (caller.GrantId is { } grantId)
            return await _workItems.GetWorkItemAsync(workItemId) is { } own && own.CreatedByExternalGrantId != grantId
                ? "A grant reaches only the work items it created for this route."
                : null;
        if (caller.RunId is not { } runId)
            return null;
        if (await _workItems.GetWorkItemAsync(workItemId) is not { } workItem)
            return null;
        if (workItem.CreatedByLoopRunId == runId)
            return null;
        var ownWorkItemId = await _db.LoopRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.WorkItemId).FirstOrDefaultAsync();
        return ownWorkItemId == workItem.Id ? null : "This work item belongs to another run.";
    }

    public async Task<ProposerResolution> ResolveProposerAsync(AgentCaller caller)
    {
        if (caller.RunId is { } runId)
            return await RunExistsAsync(runId)
                ? Resolved(new AgentProposer(runId, null, null))
                : Unknown("Unknown loop run.");

        if (caller.ChatSessionId is { } chatSessionId)
            return await ChatSessionExistsAsync(chatSessionId)
                ? Resolved(new AgentProposer(null, chatSessionId, null))
                : Unknown("Unknown or inactive chat session.");

        if (caller.GrantId is { } grantId)
            return await _db.ExternalAgentGrants.AsNoTracking().AnyAsync(g => g.Id == grantId)
                ? Resolved(new AgentProposer(null, null, grantId))
                : Unknown("Unknown external-agent grant.");

        return new(ProposerOutcome.NoSession, null,
            "A proposal needs the proposing session. Only an agent launched for a run or a chat, or an external-agent grant, has one.");
    }

    public async Task<ProposalStamp> StampAsync(AgentProposer proposer)
        => new(
            proposer.RunId,
            proposer.ChatSessionId,
            proposer.GrantId,
            proposer.RunId is { } runId ? await _runs.GetRunningNodeIdAsync(runId) : null,
            proposer.ChatSessionId is { } chatSessionId ? await ChatReplySequenceAsync(chatSessionId) : null);

    public Task<bool> RunExistsAsync(Guid runId) => _db.LoopRuns.AsNoTracking().AnyAsync(r => r.Id == runId);

    public Task<bool> ChatSessionExistsAsync(Guid chatSessionId) => _db.ChatSessions.AsNoTracking().AnyAsync(c => c.Id == chatSessionId);

    public Task<bool> RunWorksOnAsync(Guid runId, string workItemId)
        => _db.LoopRuns.AsNoTracking().AnyAsync(r => r.Id == runId && r.WorkItemId == workItemId);

    /// <summary>
    /// The sequence of the reply a chat proposal follows. A chat persists the user's message before its turn starts,
    /// so a newest user message means the proposal comes from that turn, whose reply takes the next sequence.
    /// </summary>
    private async Task<int?> ChatReplySequenceAsync(Guid chatSessionId)
    {
        var newest = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatSessionId == chatSessionId)
            .OrderByDescending(m => m.Sequence)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m => new { m.Role, m.Sequence })
            .FirstOrDefaultAsync();
        if (newest is null) return null;
        return newest.Role == "user" ? newest.Sequence + 1 : newest.Sequence;
    }

    private static ProposerResolution Resolved(AgentProposer proposer) => new(ProposerOutcome.Resolved, proposer, null);

    private static ProposerResolution Unknown(string error) => new(ProposerOutcome.Unknown, null, error);
}
