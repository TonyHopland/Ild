using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class GlobalVariableProposalStore : IGlobalVariableProposalStore
{
    private readonly AppDbContext _db;
    private readonly AgentProposals<GlobalVariableProposal, GlobalVariableProposalStatus> _proposals;

    public GlobalVariableProposalStore(AppDbContext db)
    {
        _db = db;
        _proposals = new(db);
    }

    public async Task<GlobalVariableProposal> ProposeAsync(GlobalVariableProposal proposal, CancellationToken ct)
    {
        proposal.NormalizedName = GlobalVariable.Normalize(proposal.Name);
        return await _proposals.ProposeAsync(
            proposal,
            p => p.NormalizedName == proposal.NormalizedName,
            async ct =>
            {
                proposal.SnapshotUpdatedAt = await _db.GlobalVariables.AsNoTracking()
                    .Where(g => g.NormalizedName == proposal.NormalizedName)
                    .Select(g => (DateTime?)g.UpdatedAt)
                    .FirstOrDefaultAsync(ct);
                return null;
            },
            () => proposal,
            ct);
    }

    public async Task<IReadOnlyList<GlobalVariableProposalEntry>> ListAsync(GlobalVariableProposalQuery query, CancellationToken ct)
    {
        var matching = _proposals.Matching(query.Status, query.CreatedByLoopRunId, query.CreatedByChatSessionId, query.WorkItemId);
        return await EntriesAsync(await _proposals.NewestFirstAsync(matching, ct), ct);
    }

    public async Task<GlobalVariableProposalEntry?> GetAsync(Guid id, CancellationToken ct)
        => await _proposals.FindAsync(id, ct) is { } row ? (await EntriesAsync([row], ct))[0] : null;

    public Task<int> CountPendingAsync(CancellationToken ct)
        => _db.GlobalVariableProposals.CountAsync(p => p.Status == GlobalVariableProposalStatus.Pending, ct);

    public async Task<GlobalVariableProposalDecision> ApproveAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var proposal = await _proposals.FindAsync(id, ct);
        if (proposal is null) return NotFound;
        if (proposal.Status != GlobalVariableProposalStatus.Pending)
            return await RefusedAsync(id, ct);

        var now = DateTime.UtcNow;
        var applied = proposal.SnapshotUpdatedAt is { } snapshot
            ? await _db.GlobalVariables
                .Where(g => g.NormalizedName == proposal.NormalizedName && g.UpdatedAt == snapshot)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.Value, proposal.Value)
                    .SetProperty(g => g.UpdatedAt, now), ct)
            : await CreateUnlessPresentAsync(proposal, now, ct);

        if (applied == 0)
        {
            if (await _proposals.DecideAsync(id, GlobalVariableProposalStatus.Stale, null, now, ct) == 0)
                return await RefusedAsync(id, ct);
            await transaction.CommitAsync(ct);
            return new(GlobalVariableProposalDecisionOutcome.Stale, await GetAsync(id, ct), AgentProposals.ChangedChats(proposal));
        }

        // Decided concurrently: undo the write.
        if (await _proposals.DecideAsync(id, GlobalVariableProposalStatus.Approved, null, now, ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return await RefusedAsync(id, ct);
        }

        // Every other pending suggestion was made against the value just replaced.
        var staled = await _proposals.StalePendingAsync(p => p.NormalizedName == proposal.NormalizedName, now, ct);
        await transaction.CommitAsync(ct);
        return new(
            GlobalVariableProposalDecisionOutcome.Approved,
            await GetAsync(id, ct),
            AgentProposals.ChangedChats(proposal, staled));
    }

    public async Task<GlobalVariableProposalDecision> RejectAsync(Guid id, string? reason, CancellationToken ct)
    {
        if (await _proposals.RejectAsync(id, reason, ct) == 0)
            return await RefusedAsync(id, ct);
        var entry = await GetAsync(id, ct);
        return new(
            GlobalVariableProposalDecisionOutcome.Rejected,
            entry,
            AgentProposals.ChangedChats(entry?.Proposal));
    }

    /// <summary>
    /// Create the variable a "missing" snapshot expects to still be missing, in
    /// one statement: a variable created since makes it a no-op (0 rows) rather
    /// than a unique violation, which would abort the transaction on Postgres.
    /// </summary>
    private Task<int> CreateUnlessPresentAsync(GlobalVariableProposal proposal, DateTime now, CancellationToken ct)
        => _db.Database.ExecuteSqlAsync($"""
            INSERT INTO "GlobalVariables" ("Id", "Name", "NormalizedName", "Value", "UpdatedAt")
            VALUES ({Guid.NewGuid()}, {proposal.Name}, {proposal.NormalizedName}, {proposal.Value}, {now})
            ON CONFLICT ("NormalizedName") DO NOTHING
            """, ct);

    /// <summary>A decision that did not land: the suggestion was decided first, or does not exist.</summary>
    private async Task<GlobalVariableProposalDecision> RefusedAsync(Guid id, CancellationToken ct)
        => await GetAsync(id, ct) is { } current
            ? new(GlobalVariableProposalDecisionOutcome.NotPending, current, [])
            : NotFound;

    private static readonly GlobalVariableProposalDecision NotFound = new(GlobalVariableProposalDecisionOutcome.NotFound, null, []);

    private async Task<IReadOnlyList<GlobalVariableProposalEntry>> EntriesAsync(
        IReadOnlyList<GlobalVariableProposal> rows, CancellationToken ct)
    {
        var names = rows.Select(p => p.NormalizedName).Distinct().ToList();
        var variables = await _db.GlobalVariables.AsNoTracking()
            .Where(g => names.Contains(g.NormalizedName))
            .ToDictionaryAsync(g => g.NormalizedName, ct);
        var sources = await _proposals.SourcesAsync(rows, ct);

        return rows.Select(p => new GlobalVariableProposalEntry(
            p,
            variables.GetValueOrDefault(p.NormalizedName),
            sources.WorkItemOf(p),
            sources.ChatOwnerOf(p))).ToList();
    }
}
