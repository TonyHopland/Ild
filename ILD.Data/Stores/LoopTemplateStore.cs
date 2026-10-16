using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ILD.Data.Stores;

public class LoopTemplateStore : ILoopTemplateStore
{
    private readonly AppDbContext _db;

    public LoopTemplateStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<LoopTemplate?> GetByIdAsync(Guid id)
        => await _db.LoopTemplates.FindAsync(id).AsTask();

    public async Task<LoopTemplate?> GetByVersionIdAsync(Guid versionId)
        => await _db.LoopTemplates
            .FirstOrDefaultAsync(t => t.Versions.Any(v => v.Id == versionId));

    public async Task<TemplateGraph?> GetTemplateGraphByVersionIdAsync(Guid versionId)
    {
        var version = await _db.LoopTemplateVersions
            .AsNoTracking()
            .Include(v => v.LoopTemplate)
            .Include(v => v.Nodes)
                .ThenInclude(n => n.OutgoingEdges)
            .FirstOrDefaultAsync(v => v.Id == versionId);
        if (version == null) return null;
        var nodes = version.Nodes.ToList();
        var edges = nodes.SelectMany(n => n.OutgoingEdges).Distinct().ToList();
        return new TemplateGraph(version.LoopTemplate, version, nodes, edges);
    }

    public async Task<IReadOnlyList<LoopTemplate>> GetAllAsync(int skip = 0, int take = 100, bool includeArchived = false)
    {
        var query = _db.LoopTemplates.AsQueryable();
        if (!includeArchived)
            query = query.Where(t => !t.IsArchived);
        return await query.OrderBy(t => t.Name).Skip(skip).Take(take).ToListAsync();
    }

    public async Task<LoopTemplateVersion?> GetVersionByIdAsync(Guid versionId)
        => await _db.LoopTemplateVersions.FindAsync(versionId).AsTask();

    public async Task<LoopTemplateVersion?> GetLatestVersionAsync(Guid templateId)
        => await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync();

    public async Task<LoopTemplateVersion?> GetVersionAsync(Guid templateId, int versionNumber)
        => await _db.LoopTemplateVersions
            .FirstOrDefaultAsync(v => v.LoopTemplateId == templateId && v.VersionNumber == versionNumber);

    public async Task<IReadOnlyList<LoopTemplateVersion>> GetVersionsAsync(Guid templateId)
        => await _db.LoopTemplateVersions.Where(v => v.LoopTemplateId == templateId).ToListAsync();

    public async Task<IReadOnlyList<LoopNode>> GetNodesForVersionAsync(Guid versionId)
        => await _db.LoopNodes.Where(n => n.LoopTemplateVersionId == versionId).ToListAsync();

    public async Task<IReadOnlyList<LatestNodeConfig>> GetLatestNodeConfigsOfLiveTemplatesAsync()
        => await (
                from t in _db.LoopTemplates
                where !t.IsArchived
                join v in _db.LoopTemplateVersions on t.Id equals v.LoopTemplateId
                where v.VersionNumber == _db.LoopTemplateVersions
                    .Where(o => o.LoopTemplateId == t.Id)
                    .Max(o => o.VersionNumber)
                join n in _db.LoopNodes on v.Id equals n.LoopTemplateVersionId
                orderby t.Name, t.Id
                select new LatestNodeConfig(t.Id, t.Name, n.Config))
            .AsNoTracking()
            .ToListAsync();

    public async Task<IReadOnlyList<LoopNodeEdge>> GetEdgesForVersionAsync(Guid versionId)
    {
        return await _db.LoopNodeEdges
            .Where(e => _db.LoopNodes.Any(n => n.Id == e.SourceNodeId && n.LoopTemplateVersionId == versionId))
            .ToListAsync();
    }

    public Task CreateTemplateAsync(LoopTemplate template)
    {
        _db.LoopTemplates.Add(template);
        return Task.CompletedTask;
    }

    public Task UpdateTemplateAsync(LoopTemplate template)
    {
        _db.LoopTemplates.Update(template);
        return Task.CompletedTask;
    }

    public async Task SetArchivedAsync(Guid templateId, bool archived)
    {
        var template = await _db.LoopTemplates.FindAsync(templateId);
        if (template != null)
        {
            template.IsArchived = archived;
        }
    }

    public async Task<StaledLoopTemplateProposals> DeleteTemplateAsync(LoopTemplate template)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.LoopTemplates.Remove(template);
        await _db.SaveChangesAsync();
        var staled = await LoopTemplateProposalStore.StalePendingAsync(_db, template.Id, DateTime.UtcNow, CancellationToken.None);
        await tx.CommitAsync();
        return staled;
    }

    public async Task<LoopTemplateVersionSave> AddVersionAsync(LoopTemplateVersionWrite write, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var template = await _db.LoopTemplates.FindAsync([write.TemplateId], ct);
        if (template is null)
            return Unsaved(LoopTemplateVersionSaveOutcome.TemplateNotFound);

        // Written first: on Postgres the row lock this takes queues the save behind
        // any other save or proposal of the template still in flight, so the latest
        // version read below is the one they committed.
        template.Name = write.Name;
        template.Description = write.Description;
        template.RecoveryPolicy = write.RecoveryPolicy;
        template.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.Entry(template).State = EntityState.Detached;
            return Unsaved(LoopTemplateVersionSaveOutcome.TemplateNotFound);
        }

        var latest = await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == template.Id)
            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;
        var proposal = write.FromProposalId is { } proposalId
            ? await _db.LoopTemplateProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId, ct)
            : null;
        var applies = proposal is { Status: LoopTemplateProposalStatus.Pending }
            && proposal.LoopTemplateId == template.Id
            && proposal.BaseVersion == latest;
        if (write.RequireProposal && !applies)
        {
            await RollBackAsync(tx, template, write);
            return proposal is { Status: LoopTemplateProposalStatus.Pending } && proposal.LoopTemplateId == template.Id
                ? await StaleAfterConflictAsync(write)
                : Unsaved(LoopTemplateVersionSaveOutcome.ProposalNotPending);
        }

        write.Version.LoopTemplateId = template.Id;
        write.Version.VersionNumber = latest + 1;
        _db.LoopTemplateVersions.Add(write.Version);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A version row can only trip the (template, number) unique index:
            // another save took this number after the read above.
            await RollBackAsync(tx, template, write);
            return await StaleAfterConflictAsync(write);
        }
        _db.LoopNodes.AddRange(write.Nodes);
        _db.LoopNodeEdges.AddRange(write.Edges);
        await _db.SaveChangesAsync(ct);

        var now = DateTime.UtcNow;
        var applied = applies && await _db.LoopTemplateProposals
            .Where(p => p.Id == proposal!.Id && p.Status == LoopTemplateProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, LoopTemplateProposalStatus.Applied)
                .SetProperty(p => p.EditedBeforeSave, !LoopTemplateChanges.Equivalent(proposal!.Document, write.Document))
                .SetProperty(p => p.AppliedVersion, write.Version.VersionNumber)
                .SetProperty(p => p.DecidedAt, now), ct) == 1;
        if (write.RequireProposal && !applied)
        {
            // Decided concurrently: undo the write.
            await RollBackAsync(tx, template, write);
            return Unsaved(LoopTemplateVersionSaveOutcome.ProposalNotPending);
        }

        var staled = await LoopTemplateProposalStore.StalePendingAsync(_db, template.Id, now, ct);
        await tx.CommitAsync(ct);
        var appliedChat = applied && proposal!.CreatedByChatSessionId is { } chat ? new[] { chat } : [];
        return new(
            LoopTemplateVersionSaveOutcome.Saved,
            write.Version.VersionNumber,
            applied,
            staled.Count + (applied ? 1 : 0),
            appliedChat.Concat(staled.ChatSessionIds).Distinct().ToList());
    }

    /// <summary>
    /// The save lost to another version. A proposal's own Save makes the proposal
    /// Stale, in a statement of its own after the rollback (Postgres refuses any
    /// statement in a transaction a failed insert aborted).
    /// </summary>
    private async Task<LoopTemplateVersionSave> StaleAfterConflictAsync(LoopTemplateVersionWrite write)
    {
        if (!write.RequireProposal || write.FromProposalId is not { } proposalId)
            return Unsaved(LoopTemplateVersionSaveOutcome.Conflict);
        var staled = await LoopTemplateProposalStore.DecideAsync(
            _db, proposalId, LoopTemplateProposalStatus.Stale, null, DateTime.UtcNow, CancellationToken.None);
        var chat = staled == 0
            ? null
            : await _db.LoopTemplateProposals.Where(p => p.Id == proposalId).Select(p => p.CreatedByChatSessionId).FirstOrDefaultAsync();
        return new(LoopTemplateVersionSaveOutcome.Conflict, null, false, staled, chat is { } id ? [id] : []);
    }

    /// <summary>Roll back, and forget what was tracked for the save so no later save of this context writes it.</summary>
    private async Task RollBackAsync(IDbContextTransaction tx, LoopTemplate template, LoopTemplateVersionWrite write)
    {
        await tx.RollbackAsync(CancellationToken.None);
        foreach (var entity in new object[] { template, write.Version }.Concat(write.Nodes).Concat(write.Edges))
            _db.Entry(entity).State = EntityState.Detached;
    }

    private static LoopTemplateVersionSave Unsaved(LoopTemplateVersionSaveOutcome outcome) => new(outcome, null, false, 0, []);

    public Task CreateVersionAsync(LoopTemplateVersion version)
    {
        _db.LoopTemplateVersions.Add(version);
        return Task.CompletedTask;
    }

    public Task CreateNodesAsync(IReadOnlyList<LoopNode> nodes)
    {
        _db.LoopNodes.AddRange(nodes);
        return Task.CompletedTask;
    }

    public Task CreateEdgesAsync(IReadOnlyList<LoopNodeEdge> edges)
    {
        _db.LoopNodeEdges.AddRange(edges);
        return Task.CompletedTask;
    }

    public async Task DeleteNodesForVersionAsync(Guid versionId)
    {
        var nodes = await _db.LoopNodes.Where(n => n.LoopTemplateVersionId == versionId).ToListAsync();
        _db.LoopNodes.RemoveRange(nodes);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteEdgesForVersionAsync(Guid versionId)
    {
        var edges = await _db.LoopNodeEdges
            .Where(e => _db.LoopNodes.Any(n => n.Id == e.SourceNodeId && n.LoopTemplateVersionId == versionId))
            .ToListAsync();
        _db.LoopNodeEdges.RemoveRange(edges);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteVersionsForTemplateAsync(Guid templateId)
    {
        var versions = await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .ToListAsync();
        _db.LoopTemplateVersions.RemoveRange(versions);
        await _db.SaveChangesAsync();
    }

    public async Task SaveChangesAsync()
    {
        await _db.SaveChangesAsync();
    }
}
