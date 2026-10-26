using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// Human Input Proposals (ADR-0030): agents suggest the answer to the question a
/// work item's run is parked on at a Human node, and a human approves or rejects
/// it. The store only moves a suggestion between statuses; delivering the answer
/// is the decision service's job.
/// </summary>
public interface IHumanInputProposalStore
{
    /// <summary>
    /// Store a new pending suggestion; its source's earlier pending suggestion
    /// for the same work item is superseded in the same transaction.
    /// </summary>
    Task<HumanInputProposal> ProposeAsync(HumanInputProposal proposal, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<HumanInputProposal>> ListForWorkItemAsync(string workItemId, HumanInputProposalStatus? status, CancellationToken ct);

    Task<HumanInputProposal?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Pending to Approved for <paramref name="decidedBy"/>, once: the rows written (0 when it was no longer pending).</summary>
    Task<int> ClaimApprovalAsync(Guid id, string decidedBy, CancellationToken ct);

    /// <summary>Approved to Stale, for an approval whose answer was refused. The rows written.</summary>
    Task<int> RevertToStaleAsync(Guid id, CancellationToken ct);

    /// <summary>Pending to Rejected, keeping the trimmed reason (null when empty). The rows written.</summary>
    Task<int> RejectAsync(Guid id, string? reason, string decidedBy, CancellationToken ct);

    /// <summary>Every pending suggestion for the visit goes Stale. The rows written.</summary>
    Task<int> StalePendingForVisitAsync(Guid runNodeId, CancellationToken ct);
}
