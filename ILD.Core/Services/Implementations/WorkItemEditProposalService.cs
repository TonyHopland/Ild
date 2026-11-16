using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations;

public sealed class WorkItemEditProposalService : IWorkItemEditProposalService
{
    private readonly IWorkItemManager _workItems;
    private readonly IAgentSessionResolver _sessions;
    private readonly IChatNotifier _chatNotifier;

    public WorkItemEditProposalService(IWorkItemManager workItems, IAgentSessionResolver sessions, IChatNotifier chatNotifier)
    {
        _workItems = workItems;
        _sessions = sessions;
        _chatNotifier = chatNotifier;
    }

    public async Task<WorkItemEditProposalResult> ProposeAsync(
        string workItemId, AgentWorkItemEditProposalRequest request, AgentCaller caller, AgentProposer proposer, CancellationToken ct)
    {
        if (request.Title is null && request.Description is null && request.Tags is null
            && request.BranchNameOverride is null && request.BaseBranchOverride is null
            && request.AddDependencies is null && request.RemoveDependencies is null)
            return Invalid("Propose at least one of title, description, tags, branchNameOverride, baseBranchOverride, addDependencies or removeDependencies.");
        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
            return Invalid("A proposed title cannot be blank.");
        if (BranchNameRules.ValidateOverrides(request.BranchNameOverride, request.BaseBranchOverride) is { } branchError)
            return Invalid(branchError);

        var addDependencies = TrimIds(request.AddDependencies);
        var removeDependencies = TrimIds(request.RemoveDependencies);
        if (await CheckDependenciesAsync(workItemId, addDependencies, removeDependencies) is { } refused)
            return refused;

        if (await _sessions.ActorAsync(caller) is not { } actor)
            return new(WorkItemEditProposalOutcome.Unauthorized);
        var proposal = new RemoteCreateEditProposalRequest
        {
            Title = request.Title,
            Description = request.Description,
            Tags = request.Tags,
            BranchNameOverride = request.BranchNameOverride,
            BaseBranchOverride = request.BaseBranchOverride,
            AddDependencies = addDependencies,
            RemoveDependencies = removeDependencies,
            Rationale = request.Rationale,
        };
        (await _sessions.StampAsync(proposer)).ApplyTo(proposal);
        var result = await _workItems.ProposeEditAsync(actor, workItemId, proposal, ct);

        switch (result.Outcome)
        {
            case EditProposalCreateOutcome.Created:
                if (proposer.ChatSessionId is { } chat)
                    await _chatNotifier.EditProposalsChangedAsync(chat);
                return new(WorkItemEditProposalOutcome.Created, Proposal: result.Proposal);
            case EditProposalCreateOutcome.NotFound:
                return new(WorkItemEditProposalOutcome.NotFound);
            case EditProposalCreateOutcome.TooManyPending:
                return new(WorkItemEditProposalOutcome.TooManyPending, result.Error);
            default:
                return Invalid(result.Error);
        }
    }

    private static WorkItemEditProposalResult Invalid(string? error) => new(WorkItemEditProposalOutcome.Invalid, error);

    private static List<string>? TrimIds(List<string>? ids) => ids?.Select(i => i?.Trim() ?? string.Empty).ToList();

    private async Task<WorkItemEditProposalResult?> CheckDependenciesAsync(string id, List<string>? add, List<string>? remove)
    {
        if (add is null && remove is null) return null;
        var named = (add ?? []).Concat(remove ?? []).ToList();
        if (named.Any(d => d.Length == 0))
            return Invalid("A dependency id cannot be blank.");
        if (named.Distinct().Count() != named.Count)
            return Invalid("Each dependency may appear only once across addDependencies and removeDependencies.");

        if (await _workItems.GetWorkItemAsync(id) == null) return new(WorkItemEditProposalOutcome.NotFound);
        var current = (await _workItems.GetDependenciesAsync(id)).Select(d => d.Id).ToHashSet();
        foreach (var dep in remove ?? [])
        {
            if (!current.Contains(dep))
                return Invalid($"Work item {id} does not depend on {dep}, so there is nothing to remove.");
        }
        foreach (var dep in add ?? [])
        {
            if (current.Contains(dep))
                return Invalid($"Work item {id} already depends on {dep}.");
            if (await _workItems.CheckNewDependencyAsync(id, dep) is { } problem)
                return Invalid(problem);
        }
        return null;
    }
}
