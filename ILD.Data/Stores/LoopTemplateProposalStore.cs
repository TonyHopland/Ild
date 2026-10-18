using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public sealed class LoopTemplateProposalStore : ILoopTemplateProposalStore
{
    private readonly AppDbContext _db;
    private readonly AgentProposals<LoopTemplateProposal, LoopTemplateProposalStatus> _proposals;

    public LoopTemplateProposalStore(AppDbContext db)
    {
        _db = db;
        _proposals = new(db);
    }

    public Task<LoopTemplateProposeResult> ProposeAsync(LoopTemplateProposal proposal, CancellationToken ct = default)
        => _proposals.ProposeAsync(
            proposal,
            p => p.LoopTemplateId == proposal.LoopTemplateId,
            ct => RefusalAsync(proposal, ct),
            () => new LoopTemplateProposeResult(LoopTemplateProposeOutcome.Proposed, proposal, proposal.BaseVersion),
            ct);

    /// <summary>Why <paramref name="proposal"/> cannot be stored now; null when it can.</summary>
    private async Task<LoopTemplateProposeResult?> RefusalAsync(LoopTemplateProposal proposal, CancellationToken ct)
    {
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
        return latest == proposal.BaseVersion ? null : new(LoopTemplateProposeOutcome.NotLatest, null, latest);
    }

    public async Task<IReadOnlyList<LoopTemplateProposalEntry>> ListAsync(LoopTemplateProposalQuery query, CancellationToken ct = default)
    {
        var matching = _proposals.Matching(query.Status, query.CreatedByLoopRunId, query.CreatedByChatSessionId, query.WorkItemId);
        if (query.TemplateId is { } templateId) matching = matching.Where(p => p.LoopTemplateId == templateId);
        return await EntriesAsync(await _proposals.NewestFirstAsync(matching, ct), ct);
    }

    public async Task<LoopTemplateProposalEntry?> GetAsync(Guid id, CancellationToken ct = default)
        => await _proposals.FindAsync(id, ct) is { } row ? (await EntriesAsync([row], ct))[0] : null;

    public async Task<IReadOnlyDictionary<Guid, int>> CountPendingByTemplateAsync(CancellationToken ct = default)
        => await _db.LoopTemplateProposals
            .Where(p => p.Status == LoopTemplateProposalStatus.Pending)
            .GroupBy(p => p.LoopTemplateId)
            .Select(g => new { TemplateId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TemplateId, g => g.Count, ct);

    public async Task<LoopTemplateProposalDecision> RejectAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        if (await _proposals.RejectAsync(id, reason, ct) == 0)
            return await RefusedAsync(id, ct);
        var entry = await GetAsync(id, ct);
        return new(
            LoopTemplateProposalDecisionOutcome.Rejected,
            entry,
            AgentProposals.ChangedChats(entry?.Proposal));
    }

    /// <summary>A decision that did not land: the proposal was decided first, or does not exist.</summary>
    private async Task<LoopTemplateProposalDecision> RefusedAsync(Guid id, CancellationToken ct)
        => await GetAsync(id, ct) is { } current
            ? new(LoopTemplateProposalDecisionOutcome.NotPending, current, [])
            : new(LoopTemplateProposalDecisionOutcome.NotFound, null, []);

    private async Task<IReadOnlyList<LoopTemplateProposalEntry>> EntriesAsync(
        IReadOnlyList<LoopTemplateProposal> rows, CancellationToken ct)
    {
        var templateIds = rows.Select(p => p.LoopTemplateId).Distinct().ToList();

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
        var sources = await _proposals.SourcesAsync(rows, ct);

        return rows.Select(p => new LoopTemplateProposalEntry(
            p,
            templates.GetValueOrDefault(p.LoopTemplateId),
            latest.TryGetValue(p.LoopTemplateId, out var number) ? number : null,
            graphs.GetValueOrDefault((p.LoopTemplateId, p.BaseVersion)),
            sources.WorkItemOf(p),
            sources.ChatOwnerOf(p))).ToList();
    }
}
