using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ILD.Data.Stores;

public sealed class GlobalVariableProposalStore : IGlobalVariableProposalStore
{
    private readonly AppDbContext _db;

    public GlobalVariableProposalStore(AppDbContext db) { _db = db; }

    /// <summary>Enough for a few suggestions from one source arriving at once; each round lets one through.</summary>
    private const int MaxProposeAttempts = 3;

    public async Task<GlobalVariableProposal> ProposeAsync(GlobalVariableProposal proposal, CancellationToken ct = default)
    {
        proposal.NormalizedName = GlobalVariable.Normalize(proposal.Name);
        proposal.Status = GlobalVariableProposalStatus.Pending;
        var runId = proposal.CreatedByLoopRunId;
        var chatId = proposal.CreatedByChatSessionId;

        // A suggestion from the same source racing this one past the supersede
        // step trips the pending index; the next attempt supersedes it instead.
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var now = DateTime.UtcNow;
            proposal.Id = Guid.NewGuid();
            proposal.CreatedAt = now;
            proposal.SnapshotUpdatedAt = await _db.GlobalVariables.AsNoTracking()
                .Where(g => g.NormalizedName == proposal.NormalizedName)
                .Select(g => (DateTime?)g.UpdatedAt)
                .FirstOrDefaultAsync(ct);

            await _db.GlobalVariableProposals
                .Where(p => p.NormalizedName == proposal.NormalizedName
                    && p.Status == GlobalVariableProposalStatus.Pending
                    && (runId != null ? p.CreatedByLoopRunId == runId : p.CreatedByChatSessionId == chatId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, GlobalVariableProposalStatus.Superseded)
                    .SetProperty(p => p.DecidedAt, now), ct);

            _db.GlobalVariableProposals.Add(proposal);
            try
            {
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return proposal;
            }
            catch (DbUpdateException ex) when (attempt < MaxProposeAttempts
                && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                _db.Entry(proposal).State = EntityState.Detached;
            }
        }
    }

    public async Task<IReadOnlyList<GlobalVariableProposalEntry>> ListAsync(GlobalVariableProposalQuery query, CancellationToken ct = default)
    {
        IQueryable<GlobalVariableProposal> q = _db.GlobalVariableProposals.AsNoTracking();
        if (query.Status is { } status) q = q.Where(p => p.Status == status);
        if (query.CreatedByLoopRunId is { } runId) q = q.Where(p => p.CreatedByLoopRunId == runId);
        if (query.CreatedByChatSessionId is { } chatId) q = q.Where(p => p.CreatedByChatSessionId == chatId);
        if (query.WorkItemId is { } workItemId)
            q = q.Where(p => _db.LoopRuns.Any(r => r.Id == p.CreatedByLoopRunId && r.WorkItemId == workItemId));

        var rows = await q.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).ToListAsync(ct);
        return await EntriesAsync(rows, ct);
    }

    public async Task<GlobalVariableProposalEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.GlobalVariableProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? null : (await EntriesAsync([row], ct))[0];
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default)
        => _db.GlobalVariableProposals.CountAsync(p => p.Status == GlobalVariableProposalStatus.Pending, ct);

    public async Task<GlobalVariableProposalDecision> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var proposal = await _db.GlobalVariableProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
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
        var ownChat = proposal.CreatedByChatSessionId is { } chat ? new[] { chat } : [];

        if (applied == 0)
        {
            if (await DecideAsync(id, GlobalVariableProposalStatus.Stale, null, now, ct) == 0)
                return await RefusedAsync(id, ct);
            await transaction.CommitAsync(ct);
            return new(GlobalVariableProposalDecisionOutcome.Stale, await GetAsync(id, ct), ownChat);
        }

        // Decided concurrently: undo the write.
        if (await DecideAsync(id, GlobalVariableProposalStatus.Approved, null, now, ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return await RefusedAsync(id, ct);
        }

        // Every other pending suggestion was made against the value just replaced.
        var staled = await StalePendingAsync(_db, proposal.NormalizedName, now, ct);
        await transaction.CommitAsync(ct);
        return new(
            GlobalVariableProposalDecisionOutcome.Approved,
            await GetAsync(id, ct),
            ownChat.Concat(staled.ChatSessionIds).Distinct().ToList());
    }

    public async Task<GlobalVariableProposalDecision> RejectAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var trimmed = reason is null ? null : AppDbContext.WithoutNul(reason).Trim();
        if (await DecideAsync(id, GlobalVariableProposalStatus.Rejected, trimmed is "" ? null : trimmed, DateTime.UtcNow, ct) == 0)
            return await RefusedAsync(id, ct);
        var entry = await GetAsync(id, ct);
        return new(
            GlobalVariableProposalDecisionOutcome.Rejected,
            entry,
            entry?.Proposal.CreatedByChatSessionId is { } chat ? [chat] : []);
    }

    /// <summary>
    /// Mark every pending suggestion for the variable stale. Called inside the
    /// transaction of the write that changed it, so no suggestion stays pending
    /// against a value that is gone.
    /// </summary>
    internal static async Task<StaledGlobalVariableProposals> StalePendingAsync(
        AppDbContext db, string normalizedName, DateTime now, CancellationToken ct)
    {
        var pending = await db.GlobalVariableProposals.AsNoTracking()
            .Where(p => p.NormalizedName == normalizedName && p.Status == GlobalVariableProposalStatus.Pending)
            .Select(p => new { p.Id, p.CreatedByChatSessionId })
            .ToListAsync(ct);
        if (pending.Count == 0) return StaledGlobalVariableProposals.None;

        var ids = pending.Select(p => p.Id).ToList();
        var count = await db.GlobalVariableProposals
            .Where(p => ids.Contains(p.Id) && p.Status == GlobalVariableProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, GlobalVariableProposalStatus.Stale)
                .SetProperty(p => p.DecidedAt, now), ct);
        return new(count, pending.Select(p => p.CreatedByChatSessionId).OfType<Guid>().Distinct().ToList());
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

    /// <summary>
    /// Move a suggestion out of Pending, compared against Pending so that exactly
    /// one decision ever lands on it. Returns the rows written.
    /// </summary>
    private Task<int> DecideAsync(Guid id, GlobalVariableProposalStatus to, string? rejectionReason, DateTime now, CancellationToken ct)
        => _db.GlobalVariableProposals
            .Where(p => p.Id == id && p.Status == GlobalVariableProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, to)
                .SetProperty(p => p.RejectionReason, rejectionReason)
                .SetProperty(p => p.DecidedAt, now), ct);

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
        var runIds = rows.Select(p => p.CreatedByLoopRunId).OfType<Guid>().Distinct().ToList();
        var chatIds = rows.Select(p => p.CreatedByChatSessionId).OfType<Guid>().Distinct().ToList();

        var variables = await _db.GlobalVariables.AsNoTracking()
            .Where(g => names.Contains(g.NormalizedName))
            .ToDictionaryAsync(g => g.NormalizedName, ct);
        var runs = await _db.LoopRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.WorkItemId, ct);
        var chats = await _db.ChatSessions.AsNoTracking()
            .Where(c => chatIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.UserId, ct);

        return rows.Select(p => new GlobalVariableProposalEntry(
            p,
            variables.GetValueOrDefault(p.NormalizedName),
            p.CreatedByLoopRunId is { } run ? runs.GetValueOrDefault(run) : null,
            p.CreatedByChatSessionId is { } chat ? chats.GetValueOrDefault(chat) : null)).ToList();
    }
}
