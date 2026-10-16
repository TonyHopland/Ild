using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class LoopTemplateProposalStore : ILoopTemplateProposalStore
{
    private readonly AppDbContext _db;

    public LoopTemplateProposalStore(AppDbContext db) { _db = db; }

    /// <summary>Enough for a few proposals from one source arriving at once; each round lets one through.</summary>
    private const int MaxProposeAttempts = 3;

    public async Task<LoopTemplateProposeResult> ProposeAsync(LoopTemplateProposal proposal, CancellationToken ct = default)
    {
        proposal.Status = LoopTemplateProposalStatus.Pending;
        var runId = proposal.CreatedByLoopRunId;
        var chatId = proposal.CreatedByChatSessionId;

        // A proposal from the same source racing this one past the supersede
        // step trips the pending index; the next attempt supersedes it instead.
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // A write that changes nothing, for the row lock it takes on Postgres:
            // a save of the template still in flight commits its version before
            // the latest version is read below, so no proposal is left pending
            // against a version a save has already replaced.
            var locked = await _db.LoopTemplates
                .Where(t => t.Id == proposal.LoopTemplateId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt), ct);
            if (locked == 0)
                return new(LoopTemplateProposeOutcome.TemplateNotFound, null, null);

            var latest = await _db.LoopTemplateVersions
                .Where(v => v.LoopTemplateId == proposal.LoopTemplateId)
                .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;
            if (latest != proposal.BaseVersion)
                return new(LoopTemplateProposeOutcome.NotLatest, null, latest);

            var now = DateTime.UtcNow;
            proposal.Id = Guid.NewGuid();
            proposal.CreatedAt = now;
            await _db.LoopTemplateProposals
                .Where(p => p.LoopTemplateId == proposal.LoopTemplateId
                    && p.Status == LoopTemplateProposalStatus.Pending
                    && (runId != null ? p.CreatedByLoopRunId == runId : p.CreatedByChatSessionId == chatId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, LoopTemplateProposalStatus.Superseded)
                    .SetProperty(p => p.DecidedAt, now), ct);

            _db.LoopTemplateProposals.Add(proposal);
            try
            {
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new(LoopTemplateProposeOutcome.Proposed, proposal, latest);
            }
            catch (DbUpdateException ex) when (attempt < MaxProposeAttempts && DbErrors.IsUniqueViolation(ex))
            {
                _db.Entry(proposal).State = EntityState.Detached;
            }
        }
    }

    public async Task<IReadOnlyList<LoopTemplateProposalEntry>> ListAsync(LoopTemplateProposalQuery query, CancellationToken ct = default)
    {
        IQueryable<LoopTemplateProposal> q = _db.LoopTemplateProposals.AsNoTracking();
        if (query.Status is { } status) q = q.Where(p => p.Status == status);
        if (query.TemplateId is { } templateId) q = q.Where(p => p.LoopTemplateId == templateId);
        if (query.CreatedByLoopRunId is { } runId) q = q.Where(p => p.CreatedByLoopRunId == runId);
        if (query.CreatedByChatSessionId is { } chatId) q = q.Where(p => p.CreatedByChatSessionId == chatId);
        if (query.WorkItemId is { } workItemId)
            q = q.Where(p => _db.LoopRuns.Any(r => r.Id == p.CreatedByLoopRunId && r.WorkItemId == workItemId));

        var rows = await q.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).ToListAsync(ct);
        return await EntriesAsync(rows, ct);
    }

    public async Task<LoopTemplateProposalEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.LoopTemplateProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? null : (await EntriesAsync([row], ct))[0];
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountPendingByTemplateAsync(CancellationToken ct = default)
        => await _db.LoopTemplateProposals
            .Where(p => p.Status == LoopTemplateProposalStatus.Pending)
            .GroupBy(p => p.LoopTemplateId)
            .Select(g => new { TemplateId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TemplateId, g => g.Count, ct);

    public async Task<LoopTemplateProposalDecision> RejectAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var trimmed = reason is null ? null : AppDbContext.WithoutNul(reason).Trim();
        if (await DecideAsync(_db, id, LoopTemplateProposalStatus.Rejected, trimmed is "" ? null : trimmed, DateTime.UtcNow, ct) == 0)
        {
            return await GetAsync(id, ct) is { } current
                ? new(LoopTemplateProposalDecisionOutcome.NotPending, current, [])
                : new(LoopTemplateProposalDecisionOutcome.NotFound, null, []);
        }
        var entry = await GetAsync(id, ct);
        return new(
            LoopTemplateProposalDecisionOutcome.Rejected,
            entry,
            entry?.Proposal.CreatedByChatSessionId is { } chat ? [chat] : []);
    }

    /// <summary>
    /// Mark every pending proposal of the template stale. Called inside the
    /// transaction of the write that moved the template on, so no proposal stays
    /// pending against a version that is no longer the latest.
    /// </summary>
    internal static async Task<StaledLoopTemplateProposals> StalePendingAsync(
        AppDbContext db, Guid templateId, DateTime now, CancellationToken ct)
    {
        var pending = await db.LoopTemplateProposals.AsNoTracking()
            .Where(p => p.LoopTemplateId == templateId && p.Status == LoopTemplateProposalStatus.Pending)
            .Select(p => new { p.Id, p.CreatedByChatSessionId })
            .ToListAsync(ct);
        if (pending.Count == 0) return StaledLoopTemplateProposals.None;

        var ids = pending.Select(p => p.Id).ToList();
        var count = await db.LoopTemplateProposals
            .Where(p => ids.Contains(p.Id) && p.Status == LoopTemplateProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, LoopTemplateProposalStatus.Stale)
                .SetProperty(p => p.DecidedAt, now), ct);
        return new(count, pending.Select(p => p.CreatedByChatSessionId).OfType<Guid>().Distinct().ToList());
    }

    /// <summary>
    /// Move a proposal out of Pending, compared against Pending so that exactly
    /// one decision ever lands on it. Returns the rows written.
    /// </summary>
    internal static Task<int> DecideAsync(
        AppDbContext db, Guid id, LoopTemplateProposalStatus to, string? rejectionReason, DateTime now, CancellationToken ct)
        => db.LoopTemplateProposals
            .Where(p => p.Id == id && p.Status == LoopTemplateProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, to)
                .SetProperty(p => p.RejectionReason, rejectionReason)
                .SetProperty(p => p.DecidedAt, now), ct);

    private async Task<IReadOnlyList<LoopTemplateProposalEntry>> EntriesAsync(
        IReadOnlyList<LoopTemplateProposal> rows, CancellationToken ct)
    {
        var templateIds = rows.Select(p => p.LoopTemplateId).Distinct().ToList();
        var runIds = rows.Select(p => p.CreatedByLoopRunId).OfType<Guid>().Distinct().ToList();
        var chatIds = rows.Select(p => p.CreatedByChatSessionId).OfType<Guid>().Distinct().ToList();

        var templates = await _db.LoopTemplates.AsNoTracking()
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        var versions = await _db.LoopTemplateVersions.AsNoTracking()
            .Where(v => templateIds.Contains(v.LoopTemplateId))
            .ToListAsync(ct);
        var latest = versions.GroupBy(v => v.LoopTemplateId).ToDictionary(g => g.Key, g => g.Max(v => v.VersionNumber));
        var bases = rows
            .Select(p => versions.FirstOrDefault(v => v.LoopTemplateId == p.LoopTemplateId && v.VersionNumber == p.BaseVersion))
            .OfType<LoopTemplateVersion>()
            .DistinctBy(v => v.Id)
            .ToList();
        var baseIds = bases.Select(v => v.Id).ToList();
        var nodes = await _db.LoopNodes.AsNoTracking()
            .Where(n => baseIds.Contains(n.LoopTemplateVersionId))
            .ToListAsync(ct);
        var edges = await _db.LoopNodeEdges.AsNoTracking()
            .Where(e => _db.LoopNodes.Any(n => n.Id == e.SourceNodeId && baseIds.Contains(n.LoopTemplateVersionId)))
            .ToListAsync(ct);
        var nodeVersion = nodes.ToDictionary(n => n.Id, n => n.LoopTemplateVersionId);
        var graphs = bases
            .Where(v => templates.ContainsKey(v.LoopTemplateId))
            .ToDictionary(v => (v.LoopTemplateId, v.VersionNumber), v => new TemplateGraph(
                templates[v.LoopTemplateId],
                v,
                nodes.Where(n => n.LoopTemplateVersionId == v.Id).ToList(),
                edges.Where(e => nodeVersion[e.SourceNodeId] == v.Id).ToList()));

        var runs = await _db.LoopRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.WorkItemId, ct);
        var chats = await _db.ChatSessions.AsNoTracking()
            .Where(c => chatIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.UserId, ct);

        return rows.Select(p => new LoopTemplateProposalEntry(
            p,
            templates.GetValueOrDefault(p.LoopTemplateId),
            latest.TryGetValue(p.LoopTemplateId, out var number) ? number : null,
            graphs.GetValueOrDefault((p.LoopTemplateId, p.BaseVersion)),
            p.CreatedByLoopRunId is { } run ? runs.GetValueOrDefault(run) : null,
            p.CreatedByChatSessionId is { } chat ? chats.GetValueOrDefault(chat) : null)).ToList();
    }
}
