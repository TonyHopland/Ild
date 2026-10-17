using System.Linq.Expressions;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace ILD.Data.Stores;

/// <summary>
/// What every kind of agent proposal does the same way, over that kind's table:
/// proposing in place of the source's pending proposal for the same target,
/// listing, deciding against Pending, and going stale. Each kind's store adds
/// what it is proposed against and what saving it writes.
/// </summary>
internal sealed class AgentProposals<TProposal, TStatus>
    where TProposal : class, IAgentProposal<TStatus>
    where TStatus : struct, Enum
{
    /// <summary>Enough for a few proposals from one source arriving at once; each round lets one through.</summary>
    private const int MaxProposeAttempts = 3;

    private readonly AppDbContext _db;
    private readonly DbSet<TProposal> _table;
    private readonly TStatus _pending;
    private readonly TStatus _rejected;
    private readonly TStatus _stale;
    private readonly TStatus _superseded;
    private readonly Expression<Func<TProposal, bool>> _isPending;

    public AgentProposals(
        AppDbContext db, DbSet<TProposal> table, TStatus pending, TStatus rejected, TStatus stale, TStatus superseded)
    {
        _db = db;
        _table = table;
        _pending = pending;
        _rejected = rejected;
        _stale = stale;
        _superseded = superseded;
        _isPending = HasStatus(pending);
    }

    /// <summary>
    /// Store <paramref name="proposal"/> as pending, superseding its source's
    /// pending proposal for the same target in the same transaction.
    /// <paramref name="admitAsync"/> runs first in that transaction; when it
    /// returns false nothing is stored and false comes back.
    /// </summary>
    public async Task<bool> ProposeAsync(
        TProposal proposal,
        Expression<Func<TProposal, bool>> sameTarget,
        Func<CancellationToken, Task<bool>> admitAsync,
        CancellationToken ct)
    {
        proposal.Status = _pending;
        var sameSource = FromSourceOf(proposal);

        // A proposal from the same source racing this one past the supersede
        // step trips the pending index; the next attempt supersedes it instead.
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            if (!await admitAsync(ct))
                return false;

            var now = DateTime.UtcNow;
            proposal.Id = Guid.NewGuid();
            proposal.CreatedAt = now;
            await _table
                .Where(sameTarget)
                .Where(sameSource)
                .Where(_isPending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, _superseded)
                    .SetProperty(p => p.DecidedAt, now), ct);

            _table.Add(proposal);
            try
            {
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            }
            catch (DbUpdateException ex) when (attempt < MaxProposeAttempts && DbErrors.IsUniqueViolation(ex))
            {
                _db.Entry(proposal).State = EntityState.Detached;
            }
        }
    }

    /// <summary>The proposals every filter set narrows to; none set matches every proposal.</summary>
    public IQueryable<TProposal> Matching(TStatus? status, Guid? createdByLoopRunId, Guid? createdByChatSessionId, string? workItemId)
    {
        IQueryable<TProposal> q = _table.AsNoTracking();
        if (status is { } s) q = q.Where(HasStatus(s));
        if (createdByLoopRunId is { } runId) q = q.Where(p => p.CreatedByLoopRunId == runId);
        if (createdByChatSessionId is { } chatId) q = q.Where(p => p.CreatedByChatSessionId == chatId);
        if (workItemId is not null)
            q = q.Where(p => _db.LoopRuns.Any(r => r.Id == p.CreatedByLoopRunId && r.WorkItemId == workItemId));
        return q;
    }

    public Task<List<TProposal>> NewestFirstAsync(IQueryable<TProposal> proposals, CancellationToken ct)
        => proposals.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).ToListAsync(ct);

    public Task<TProposal?> FindAsync(Guid id, CancellationToken ct)
        => _table.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>
    /// Move a proposal out of Pending, compared against Pending so that exactly
    /// one decision ever lands on it. <paramref name="alsoSet"/> writes what
    /// else that kind of decision records. Returns the rows written.
    /// </summary>
    public Task<int> DecideAsync(
        Guid id, TStatus to, string? rejectionReason, DateTime now, CancellationToken ct,
        Action<UpdateSettersBuilder<TProposal>>? alsoSet = null)
        => _table
            .Where(p => p.Id == id)
            .Where(_isPending)
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(p => p.Status, to)
                    .SetProperty(p => p.RejectionReason, rejectionReason)
                    .SetProperty(p => p.DecidedAt, now);
                alsoSet?.Invoke(s);
            }, ct);

    /// <summary>Reject a pending proposal, keeping the human's reason when they gave one. Returns the rows written.</summary>
    public Task<int> RejectAsync(Guid id, string? reason, CancellationToken ct)
    {
        var trimmed = reason is null ? null : AppDbContext.WithoutNul(reason).Trim();
        return DecideAsync(id, _rejected, trimmed is "" ? null : trimmed, DateTime.UtcNow, ct);
    }

    /// <summary>
    /// Mark every pending proposal of the target stale. Called inside the
    /// transaction of the write that changed the target, so no proposal stays
    /// pending against what is gone.
    /// </summary>
    public async Task<StaledProposals> StalePendingAsync(
        Expression<Func<TProposal, bool>> ofTarget, DateTime now, CancellationToken ct)
    {
        var pending = await _table.AsNoTracking()
            .Where(ofTarget)
            .Where(_isPending)
            .Select(p => new { p.Id, p.CreatedByChatSessionId })
            .ToListAsync(ct);
        if (pending.Count == 0) return StaledProposals.None;

        var ids = pending.Select(p => p.Id).ToList();
        var count = await _table
            .Where(p => ids.Contains(p.Id))
            .Where(_isPending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, _stale)
                .SetProperty(p => p.DecidedAt, now), ct);
        return new(count, pending.Select(p => p.CreatedByChatSessionId).OfType<Guid>().Distinct().ToList());
    }

    /// <summary>The sources of <paramref name="rows"/> that still exist.</summary>
    public async Task<ProposalSources> SourcesAsync(IReadOnlyList<TProposal> rows, CancellationToken ct)
    {
        var runIds = rows.Select(p => p.CreatedByLoopRunId).OfType<Guid>().Distinct().ToList();
        var chatIds = rows.Select(p => p.CreatedByChatSessionId).OfType<Guid>().Distinct().ToList();
        var runs = await _db.LoopRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.WorkItemId, ct);
        var chats = await _db.ChatSessions.AsNoTracking()
            .Where(c => chatIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.UserId, ct);
        return new(runs, chats);
    }

    private static Expression<Func<TProposal, bool>> FromSourceOf(TProposal proposal)
    {
        var runId = proposal.CreatedByLoopRunId;
        var chatId = proposal.CreatedByChatSessionId;
        return runId is not null
            ? p => p.CreatedByLoopRunId == runId
            : p => p.CreatedByChatSessionId == chatId;
    }

    /// <summary><c>p =&gt; p.Status == status</c>, which C# cannot write over a generic enum.</summary>
    private static Expression<Func<TProposal, bool>> HasStatus(TStatus status)
    {
        var p = Expression.Parameter(typeof(TProposal), "p");
        return Expression.Lambda<Func<TProposal, bool>>(
            Expression.Equal(Expression.Property(p, nameof(IAgentProposal<TStatus>.Status)), Expression.Constant(status)),
            p);
    }
}

/// <summary>
/// Where proposals came from, while that still exists: the work item of the run
/// that made one, or the owner of the chat that made it. Null once deleted.
/// </summary>
internal sealed class ProposalSources(IReadOnlyDictionary<Guid, string> runWorkItems, IReadOnlyDictionary<Guid, string> chatOwners)
{
    public string? WorkItemOf(IAgentProposal proposal)
        => proposal.CreatedByLoopRunId is { } run ? runWorkItems.GetValueOrDefault(run) : null;

    public string? ChatOwnerOf(IAgentProposal proposal)
        => proposal.CreatedByChatSessionId is { } chat ? chatOwners.GetValueOrDefault(chat) : null;
}
