using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ILD.Data.Stores;

public class LoopTemplateStore : ILoopTemplateStore
{
    private readonly AppDbContext _db;
    private readonly AgentProposals<LoopTemplateProposal, LoopTemplateProposalStatus> _proposals;

    public LoopTemplateStore(AppDbContext db)
    {
        _db = db;
        _proposals = new(db);
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

    public async Task<StaledProposals> DeleteTemplateAsync(LoopTemplate template)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.LoopTemplates.Remove(template);
        await _db.SaveChangesAsync();
        var staled = await StalePendingAsync(template.Id, DateTime.UtcNow, CancellationToken.None);
        await tx.CommitAsync();
        return staled;
    }

    public async Task<LoopTemplateVersionSave> AddVersionAsync(
        LoopTemplateVersionWrite write, Guid? fromProposalId, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        if (await WriteTemplateAsync(write, ct) is not { } template)
            return Unsaved(LoopTemplateVersionSaveOutcome.TemplateNotFound);
        var latest = await LatestVersionAsync(template.Id, ct);
        var proposal = fromProposalId is { } proposalId ? await PendingProposalAsync(proposalId, template.Id, ct) : null;

        if (!await InsertVersionAsync(template.Id, write, latest, ct))
        {
            await RollBackAsync(tx, template, write);
            return Unsaved(LoopTemplateVersionSaveOutcome.Conflict);
        }
        var now = DateTime.UtcNow;
        var applied = proposal is { } applying && applying.BaseVersion == latest && await ApplyAsync(applying, write, now, ct);
        return await CommitAsync(tx, template.Id, write, applied ? proposal : null, now, ct);
    }

    public async Task<LoopTemplateVersionSave> SaveProposalAsync(
        LoopTemplateVersionWrite write, Guid proposalId, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        if (await WriteTemplateAsync(write, ct) is not { } template)
            return Unsaved(LoopTemplateVersionSaveOutcome.TemplateNotFound);
        var latest = await LatestVersionAsync(template.Id, ct);
        if (await PendingProposalAsync(proposalId, template.Id, ct) is not { } proposal)
        {
            await RollBackAsync(tx, template, write);
            return Unsaved(LoopTemplateVersionSaveOutcome.ProposalNotPending);
        }

        if (proposal.BaseVersion != latest || !await InsertVersionAsync(template.Id, write, latest, ct))
        {
            await RollBackAsync(tx, template, write);
            return await StaleAfterConflictAsync(proposalId);
        }
        var now = DateTime.UtcNow;
        if (!await ApplyAsync(proposal, write, now, ct))
        {
            // Decided concurrently: undo the write.
            await RollBackAsync(tx, template, write);
            return Unsaved(LoopTemplateVersionSaveOutcome.ProposalNotPending);
        }
        return await CommitAsync(tx, template.Id, write, proposal, now, ct);
    }

    /// <summary>The template with the save's name, description and policy written; null when it is gone.</summary>
    private async Task<LoopTemplate?> WriteTemplateAsync(LoopTemplateVersionWrite write, CancellationToken ct)
    {
        var template = await _db.LoopTemplates.FindAsync([write.TemplateId], ct);
        if (template is null)
            return null;

        // Written first: on Postgres the row lock this takes queues the save behind
        // any other save or proposal of the template still in flight, so the latest
        // version read after it is the one they committed.
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
            return null;
        }
        return template;
    }

    private async Task<int> LatestVersionAsync(Guid templateId, CancellationToken ct)
        => await _db.LoopTemplateVersions
            .Where(v => v.LoopTemplateId == templateId)
            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;

    private Task<LoopTemplateProposal?> PendingProposalAsync(Guid proposalId, Guid templateId, CancellationToken ct)
        => _db.LoopTemplateProposals.AsNoTracking().FirstOrDefaultAsync(
            p => p.Id == proposalId && p.LoopTemplateId == templateId && p.Status == LoopTemplateProposalStatus.Pending, ct);

    /// <summary>
    /// Insert the write as the version after <paramref name="latest"/>. False when
    /// another save took that number first; nothing is rolled back here.
    /// </summary>
    private async Task<bool> InsertVersionAsync(Guid templateId, LoopTemplateVersionWrite write, int latest, CancellationToken ct)
    {
        // Saved on its own, before the nodes: if another save took this number
        // after the latest version was read, the (template, number) unique index
        // refuses it. Only that refusal is a lost race; any other failure propagates.
        write.Version.LoopTemplateId = templateId;
        write.Version.VersionNumber = latest + 1;
        _db.LoopTemplateVersions.Add(write.Version);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return false;
        }
        _db.LoopNodes.AddRange(write.Nodes);
        _db.LoopNodeEdges.AddRange(write.Edges);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Mark the proposal Applied as the version just inserted, only while it is still pending.</summary>
    private async Task<bool> ApplyAsync(LoopTemplateProposal proposal, LoopTemplateVersionWrite write, DateTime now, CancellationToken ct)
        => await _proposals.DecideAsync(
            proposal.Id,
            LoopTemplateProposalStatus.Applied,
            null,
            now,
            s => s
                .SetProperty(p => p.EditedBeforeSave, !LoopTemplateChanges.Equivalent(proposal.Document, write.Document))
                .SetProperty(p => p.AppliedVersion, write.Version.VersionNumber),
            ct) == 1;

    /// <summary>Make every other pending proposal of the template stale, and commit the save.</summary>
    private async Task<LoopTemplateVersionSave> CommitAsync(
        IDbContextTransaction tx, Guid templateId, LoopTemplateVersionWrite write, LoopTemplateProposal? applied, DateTime now, CancellationToken ct)
    {
        var staled = await StalePendingAsync(templateId, now, ct);
        await tx.CommitAsync(ct);
        return new(
            LoopTemplateVersionSaveOutcome.Saved,
            write.Version.VersionNumber,
            applied is not null,
            staled.Count + (applied is not null ? 1 : 0),
            AgentProposals.ChangedChats(applied, staled));
    }

    /// <summary>
    /// A proposal's own Save lost to another version, so the proposal goes Stale.
    /// That runs in a statement of its own after the rollback, because Postgres
    /// aborts a transaction once an insert in it has failed (short of EF's
    /// savepoint), and the save's template write must be undone anyway.
    /// </summary>
    private async Task<LoopTemplateVersionSave> StaleAfterConflictAsync(Guid proposalId)
    {
        var staled = await _proposals.DecideAsync(
            proposalId, LoopTemplateProposalStatus.Stale, null, DateTime.UtcNow, CancellationToken.None);
        var chat = staled == 0
            ? null
            : await _db.LoopTemplateProposals.Where(p => p.Id == proposalId).Select(p => p.CreatedByChatSessionId).FirstOrDefaultAsync();
        return new(LoopTemplateVersionSaveOutcome.Conflict, null, false, staled, AgentProposals.ChangedChats(chat));
    }

    /// <summary>
    /// Mark every pending proposal of the template stale, in the transaction of
    /// the write that moved it on, so none stays pending against a version that
    /// is no longer the latest.
    /// </summary>
    private Task<StaledProposals> StalePendingAsync(Guid templateId, DateTime now, CancellationToken ct)
        => _proposals.StalePendingAsync(p => p.LoopTemplateId == templateId, now, ct);

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
