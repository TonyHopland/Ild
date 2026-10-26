using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class HumanInputProposalService : IHumanInputProposalService
{
    private readonly IHumanInputProposalStore _proposals;
    private readonly ILoopRunStore _runs;
    private readonly IWorkItemManager _workItems;
    private readonly IWorkItemNotifier _notifier;

    public HumanInputProposalService(
        IHumanInputProposalStore proposals,
        ILoopRunStore runs,
        IWorkItemManager workItems,
        IWorkItemNotifier notifier)
    {
        _proposals = proposals;
        _runs = runs;
        _workItems = workItems;
        _notifier = notifier;
    }

    public async Task<HumanInputProposalResult> ProposeAsync(
        string workItemId, string output, string text, string rationale,
        Guid? runId, Guid? chatSessionId, CancellationToken ct)
    {
        if (await _workItems.GetWorkItemAsync(workItemId) is null)
            return new(HumanInputProposalOutcome.NotFound);
        if (await ParkedVisitAsync(workItemId) is not var (run, visit, offered))
            return new(HumanInputProposalOutcome.NotParked);
        if (!offered.Contains(output))
            return new(HumanInputProposalOutcome.OutputNotOffered, OfferedOutputs: offered);

        var proposal = await _proposals.ProposeAsync(new HumanInputProposal
        {
            WorkItemId = workItemId,
            RunId = run.Id,
            RunNodeId = visit.Id,
            Output = output,
            Text = text,
            Rationale = rationale,
            CreatedByLoopRunId = runId,
            CreatedByChatSessionId = chatSessionId,
        }, ct);
        await _notifier.HumanInputProposalsChangedAsync(workItemId);
        return new(HumanInputProposalOutcome.Created, proposal);
    }

    public async Task<HumanInputProposalDecision> ApproveAsync(Guid id, string decidedBy, CancellationToken ct)
    {
        if (await _proposals.GetAsync(id, ct) is not { } proposal)
            return new(HumanInputDecisionOutcome.NotFound, null);
        if (proposal.Status != HumanInputProposalStatus.Pending)
            return new(HumanInputDecisionOutcome.NotPending, proposal);

        // Claimed before delivering so that of two approvals only one answers the
        // visit; a refused delivery gives the claim back as Stale.
        if (await _proposals.ClaimApprovalAsync(id, decidedBy, ct) == 0)
            return await RefusedAsync(id, ct);

        var delivered = await DeliverAsync(proposal);
        if (!delivered)
            await _proposals.RevertToStaleAsync(id, ct);
        await _notifier.HumanInputProposalsChangedAsync(proposal.WorkItemId);
        return new(delivered ? HumanInputDecisionOutcome.Approved : HumanInputDecisionOutcome.Stale,
            await _proposals.GetAsync(id, ct));
    }

    public async Task<HumanInputProposalDecision> RejectAsync(Guid id, string? reason, string decidedBy, CancellationToken ct)
    {
        if (await _proposals.RejectAsync(id, reason, decidedBy, ct) == 0)
            return await RefusedAsync(id, ct);
        var proposal = await _proposals.GetAsync(id, ct);
        await _notifier.HumanInputProposalsChangedAsync(proposal!.WorkItemId);
        return new(HumanInputDecisionOutcome.Rejected, proposal);
    }

    private async Task<bool> DeliverAsync(HumanInputProposal proposal)
    {
        try
        {
            return await _workItems.SubmitHumanFeedbackAnswerAsync(
                proposal.WorkItemId, proposal.RunId, proposal.RunNodeId, proposal.Output, proposal.Text);
        }
        catch (Exception ex) when (ex is HumanFeedbackRefusedException or RunClosedException)
        {
            return false;
        }
    }

    private async Task<HumanInputProposalDecision> RefusedAsync(Guid id, CancellationToken ct)
        => await _proposals.GetAsync(id, ct) is { } current
            ? new(HumanInputDecisionOutcome.NotPending, current)
            : new(HumanInputDecisionOutcome.NotFound, null);

    /// <summary>
    /// The work item's active run and its waiting visit, when the run is parked
    /// for human input at a Human node, with the outputs that node offers.
    /// </summary>
    private async Task<(LoopRun Run, LoopRunNode Visit, IReadOnlyList<string> Offered)?> ParkedVisitAsync(string workItemId)
    {
        if (await _runs.GetActiveByWorkItemAsync(workItemId) is not
            { Status: LoopRunStatus.WaitingHuman, IsHalted: false, HumanFeedbackReason: HumanFeedbackReasons.HumanInputNeeded } run)
            return null;
        if (ParkedNode.WaitingVisit(await _runs.GetRunNodesAsync(run.Id), run.CurrentNodeId) is not { } visit)
            return null;
        var node = (await _runs.GetNodesForVersionAsync(run.LoopTemplateVersionId)).FirstOrDefault(n => n.Id == run.CurrentNodeId);
        if (node is not { NodeType: NodeType.Human })
            return null;
        return (run, visit, ParkedNode.Outputs(node.Id, await _runs.GetEdgesForNodeIdsAsync([node.Id])));
    }
}
