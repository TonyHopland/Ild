using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class HumanInputProposalStore : IHumanInputProposalStore
{
    private readonly AppDbContext _db;
    private readonly AgentProposals<HumanInputProposal, HumanInputProposalStatus> _proposals;

    public HumanInputProposalStore(AppDbContext db)
    {
        _db = db;
        _proposals = new(db);
    }

    public Task<HumanInputProposal> ProposeAsync(HumanInputProposal proposal, CancellationToken ct)
        => _proposals.ProposeAsync(
            proposal,
            p => p.WorkItemId == proposal.WorkItemId,
            _ => Task.FromResult<HumanInputProposal?>(null),
            () => proposal,
            ct);

    public async Task<IReadOnlyList<HumanInputProposal>> ListForWorkItemAsync(
        string workItemId, HumanInputProposalStatus? status, CancellationToken ct)
    {
        var matching = _db.HumanInputProposals.AsNoTracking().Where(p => p.WorkItemId == workItemId);
        if (status is { } s) matching = matching.Where(p => p.Status == s);
        return await _proposals.NewestFirstAsync(matching, ct);
    }

    public Task<HumanInputProposal?> GetAsync(Guid id, CancellationToken ct)
        => _proposals.FindAsync(id, ct);

    public Task<int> ClaimApprovalAsync(Guid id, string decidedBy, CancellationToken ct)
        => _proposals.DecideAsync(id, HumanInputProposalStatus.Approved, null, DateTime.UtcNow,
            s => s.SetProperty(p => p.DecidedBy, decidedBy), ct);

    public Task<int> RevertToStaleAsync(Guid id, CancellationToken ct)
        => _db.HumanInputProposals
            .Where(p => p.Id == id && p.Status == HumanInputProposalStatus.Approved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, HumanInputProposalStatus.Stale)
                .SetProperty(p => p.DecidedBy, (string?)null)
                .SetProperty(p => p.DecidedAt, DateTime.UtcNow), ct);

    public Task<int> RejectAsync(Guid id, string? reason, string decidedBy, CancellationToken ct)
        => _proposals.RejectAsync(id, reason, s => s.SetProperty(p => p.DecidedBy, decidedBy), ct);

    public async Task<int> StalePendingForRunAsync(Guid runId, CancellationToken ct)
        => (await _proposals.StalePendingAsync(p => p.RunId == runId, DateTime.UtcNow, ct)).Count;

    public async Task<int> StalePendingForVisitAsync(Guid runNodeId, CancellationToken ct)
        => (await _proposals.StalePendingAsync(p => p.RunNodeId == runNodeId, DateTime.UtcNow, ct)).Count;
}
