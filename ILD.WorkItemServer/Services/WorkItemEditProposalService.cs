using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ILD.WorkItemServer.Services;

public sealed record EditProposalCreateResult(
    EditProposalCreateOutcome Outcome,
    string? Error,
    WorkItemEditProposalDto? Proposal)
{
    public static EditProposalCreateResult Refused(EditProposalCreateOutcome outcome, string error)
        => new(outcome, error, null);
}

public sealed record EditProposalDecisionResult(
    EditProposalDecisionOutcome Outcome,
    WorkItemEditProposalDto? Proposal,
    WorkItemDto? WorkItem);

public interface IWorkItemEditProposalService
{
    /// <summary>
    /// Store a pending proposal with a snapshot of the item's editable fields.
    /// Nothing on the item changes.
    /// </summary>
    Task<EditProposalCreateResult> CreateAsync(string workItemId, CreateEditProposalRequest req, CancellationToken ct = default);

    /// <summary>The item's proposals, newest first. Null when there is no such work item.</summary>
    Task<IReadOnlyList<WorkItemEditProposalDto>?> ListForItemAsync(string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Proposals across items, newest first. <paramref name="undeliveredOnly"/>
    /// keeps the decided ones whose decision the proposing chat has not been told.
    /// </summary>
    Task<IReadOnlyList<WorkItemEditProposalDto>> ListAsync(
        WorkItemEditProposalStatus? status, Guid? createdByChatSessionId, bool undeliveredOnly, CancellationToken ct = default);

    /// <summary>
    /// Apply a pending proposal if, and only if, the item's editable fields still
    /// equal its snapshot — checked and written in one statement, so a human edit
    /// landing at any moment is never overwritten. Otherwise the proposal goes
    /// Stale. Either way no other proposal on the item stays pending once one has
    /// been applied.
    /// </summary>
    Task<EditProposalDecisionResult> ApproveAsync(string workItemId, Guid proposalId, CancellationToken ct = default);

    Task<EditProposalDecisionResult> RejectAsync(string workItemId, Guid proposalId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Record that the proposing chat has been told these decisions. A proposal
    /// still pending is left alone: it has no decision yet, and acknowledging it
    /// now would swallow the one it gets later.
    /// </summary>
    Task MarkDecisionsDeliveredAsync(IReadOnlyList<Guid> proposalIds, CancellationToken ct = default);
}

public sealed class WorkItemEditProposalService : IWorkItemEditProposalService
{
    public const int MaxPendingPerWorkItem = 20;
    public const int MaxTitleLength = 512;
    public const int MaxBranchRefLength = 256;
    public const int MaxRationaleLength = 2000;
    public const int MaxRejectionReasonLength = 2000;

    private readonly WorkItemServerDbContext _db;
    private readonly IWorkItemService _workItems;
    private readonly TimeProvider _clock;

    public WorkItemEditProposalService(WorkItemServerDbContext db, IWorkItemService workItems, TimeProvider clock)
    {
        _db = db;
        _workItems = workItems;
        _clock = clock;
    }

    public async Task<EditProposalCreateResult> CreateAsync(string workItemId, CreateEditProposalRequest req, CancellationToken ct = default)
    {
        if (Validate(req) is { } error)
            return EditProposalCreateResult.Refused(EditProposalCreateOutcome.Invalid, error);

        var key = await WorkItemRows.ResolveKeyAsync(_db, workItemId, ct);
        if (key is null)
            return EditProposalCreateResult.Refused(EditProposalCreateOutcome.NotFound, "No such work item.");

        // The cap is a count-then-insert, so two proposals arriving together are
        // serialised on the owning row, as attachment uploads are.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        if (await WorkItemRows.ClaimAsync(_db, key.Value, ct) == 0)
            return EditProposalCreateResult.Refused(EditProposalCreateOutcome.NotFound, "No such work item.");

        var pending = await _db.WorkItemEditProposals
            .CountAsync(p => p.WorkItemId == key.Value && p.Status == WorkItemEditProposalStatus.Pending, ct);
        if (pending >= MaxPendingPerWorkItem)
            return EditProposalCreateResult.Refused(
                EditProposalCreateOutcome.TooManyPending,
                $"This work item already has {MaxPendingPerWorkItem} pending edit proposals. "
                + "Wait for a human to decide them before proposing another.");

        var item = await _db.WorkItems.AsNoTracking()
            .Where(w => w.InternalId == key.Value)
            .Select(w => new { w.Title, w.Description, w.TagsJson, w.BranchNameOverride, w.BaseBranchOverride })
            .SingleAsync(ct);

        var proposal = new WorkItemEditProposal
        {
            Id = Guid.NewGuid(),
            WorkItemId = key.Value,
            ProposedTitle = req.Title?.Trim(),
            ProposedDescription = req.Description,
            ProposedTagsJson = req.Tags is null ? null : WorkItemMapper.SerializeTags(req.Tags),
            ProposedBranchNameOverride = req.BranchNameOverride?.Trim(),
            ProposedBaseBranchOverride = req.BaseBranchOverride?.Trim(),
            SnapshotTitle = item.Title,
            SnapshotDescription = item.Description,
            SnapshotTagsJson = item.TagsJson,
            SnapshotBranchNameOverride = item.BranchNameOverride,
            SnapshotBaseBranchOverride = item.BaseBranchOverride,
            Rationale = string.IsNullOrWhiteSpace(req.Rationale) ? null : req.Rationale.Trim(),
            CreatedByLoopRunId = req.CreatedByLoopRunId,
            CreatedByChatSessionId = req.CreatedByChatSessionId,
            Status = WorkItemEditProposalStatus.Pending,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.WorkItemEditProposals.Add(proposal);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new EditProposalCreateResult(EditProposalCreateOutcome.Created, null, ToDto(proposal, workItemId));
    }

    private static string? Validate(CreateEditProposalRequest req)
    {
        if (req.Title is null && req.Description is null && req.Tags is null
            && req.BranchNameOverride is null && req.BaseBranchOverride is null)
            return "Propose at least one of title, description, tags, branchNameOverride or baseBranchOverride.";
        if (req.Title is not null && string.IsNullOrWhiteSpace(req.Title))
            return "A proposed title cannot be blank.";
        if (req.Title?.Trim().Length > MaxTitleLength)
            return $"A title may be at most {MaxTitleLength} characters.";
        if (req.BranchNameOverride?.Trim().Length > MaxBranchRefLength)
            return $"A branch name may be at most {MaxBranchRefLength} characters.";
        if (req.BaseBranchOverride?.Trim().Length > MaxBranchRefLength)
            return $"A base branch may be at most {MaxBranchRefLength} characters.";
        if (req.Rationale?.Trim().Length > MaxRationaleLength)
            return $"A rationale may be at most {MaxRationaleLength} characters.";
        if (req.CreatedByLoopRunId is not null && req.CreatedByChatSessionId is not null)
            return "A proposal comes from a loop run or a chat session, never both.";
        return null;
    }

    public async Task<IReadOnlyList<WorkItemEditProposalDto>?> ListForItemAsync(string workItemId, CancellationToken ct = default)
    {
        var key = await WorkItemRows.ResolveKeyAsync(_db, workItemId, ct);
        if (key is null) return null;
        var rows = await _db.WorkItemEditProposals.AsNoTracking()
            .Where(p => p.WorkItemId == key.Value)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(p => ToDto(p, workItemId)).ToList();
    }

    public async Task<IReadOnlyList<WorkItemEditProposalDto>> ListAsync(
        WorkItemEditProposalStatus? status, Guid? createdByChatSessionId, bool undeliveredOnly, CancellationToken ct = default)
    {
        IQueryable<WorkItemEditProposal> q = _db.WorkItemEditProposals.AsNoTracking();
        if (status is { } s) q = q.Where(p => p.Status == s);
        if (createdByChatSessionId is { } chat) q = q.Where(p => p.CreatedByChatSessionId == chat);
        if (undeliveredOnly)
            q = q.Where(p => p.Status != WorkItemEditProposalStatus.Pending && p.DecisionDeliveredAt == null);

        var rows = await q
            .Join(_db.WorkItems, p => p.WorkItemId, w => w.InternalId, (p, w) => new { Proposal = p, WorkItemId = w.Id })
            .OrderByDescending(r => r.Proposal.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(r => ToDto(r.Proposal, r.WorkItemId)).ToList();
    }

    public async Task<EditProposalDecisionResult> ApproveAsync(string workItemId, Guid proposalId, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var proposal = await FindAsync(workItemId, proposalId, ct);
        if (proposal is null) return NotFound();
        if (proposal.Status != WorkItemEditProposalStatus.Pending)
            return new EditProposalDecisionResult(EditProposalDecisionOutcome.NotPending, ToDto(proposal, workItemId), null);

        var now = _clock.GetUtcNow().UtcDateTime;
        var proposedTags = proposal.ProposedTagsJson;
        var applied = await _db.WorkItems
            .Where(w => w.InternalId == proposal.WorkItemId
                && w.Title == proposal.SnapshotTitle
                && w.Description == proposal.SnapshotDescription
                && w.TagsJson == proposal.SnapshotTagsJson
                && w.BranchNameOverride == proposal.SnapshotBranchNameOverride
                && w.BaseBranchOverride == proposal.SnapshotBaseBranchOverride)
            .ExecuteUpdateAsync(s =>
            {
                if (proposal.ProposedTitle is { } title)
                    s.SetProperty(w => w.Title, title);
                if (proposal.ProposedDescription is { } description)
                    s.SetProperty(w => w.Description, description);
                if (proposedTags is not null)
                    s.SetProperty(w => w.TagsJson, proposedTags);
                if (proposal.ProposedBranchNameOverride is { } branch)
                    s.SetProperty(w => w.BranchNameOverride, WorkItemMapper.NormalizeBranchRef(branch));
                if (proposal.ProposedBaseBranchOverride is { } baseBranch)
                    s.SetProperty(w => w.BaseBranchOverride, WorkItemMapper.NormalizeBranchRef(baseBranch));
                s.SetProperty(w => w.UpdatedAt, now);
            }, ct);

        if (applied == 0)
        {
            // The item changed since the snapshot (or was deleted, which takes
            // the proposal with it).
            if (await DecideAsync(proposal.Id, WorkItemEditProposalStatus.Stale, null, now, ct) == 0)
                return await RefusedAsync(workItemId, proposalId, ct);
            await transaction.CommitAsync(ct);
            return new EditProposalDecisionResult(
                EditProposalDecisionOutcome.Stale, await ReadAsync(workItemId, proposalId, ct), null);
        }

        // Decided concurrently (a reject, or a sibling's approve): undo the write.
        if (await DecideAsync(proposal.Id, WorkItemEditProposalStatus.Approved, null, now, ct) == 0)
        {
            await transaction.RollbackAsync(ct);
            return await RefusedAsync(workItemId, proposalId, ct);
        }

        // Every other pending proposal was made against the fields just replaced.
        await _db.WorkItemEditProposals
            .Where(p => p.WorkItemId == proposal.WorkItemId && p.Status == WorkItemEditProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, WorkItemEditProposalStatus.Stale)
                .SetProperty(p => p.DecidedAt, now), ct);
        await transaction.CommitAsync(ct);

        // The write went straight to the row, so a copy this scope tracks is stale.
        var tracked = _db.ChangeTracker.Entries<WorkItem>().FirstOrDefault(e => e.Entity.InternalId == proposal.WorkItemId);
        if (tracked is not null) await tracked.ReloadAsync(ct);
        return new EditProposalDecisionResult(
            EditProposalDecisionOutcome.Applied,
            await ReadAsync(workItemId, proposalId, ct),
            await _workItems.GetAsync(workItemId, ct));
    }

    public async Task<EditProposalDecisionResult> RejectAsync(string workItemId, Guid proposalId, string? reason, CancellationToken ct = default)
    {
        var proposal = await FindAsync(workItemId, proposalId, ct);
        if (proposal is null) return NotFound();

        var trimmed = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var now = _clock.GetUtcNow().UtcDateTime;
        if (await DecideAsync(proposal.Id, WorkItemEditProposalStatus.Rejected, trimmed, now, ct) == 0)
            return await RefusedAsync(workItemId, proposalId, ct);
        return new EditProposalDecisionResult(
            EditProposalDecisionOutcome.Rejected, await ReadAsync(workItemId, proposalId, ct), null);
    }

    public async Task MarkDecisionsDeliveredAsync(IReadOnlyList<Guid> proposalIds, CancellationToken ct = default)
    {
        if (proposalIds.Count == 0) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.WorkItemEditProposals
            .Where(p => proposalIds.Contains(p.Id)
                && p.Status != WorkItemEditProposalStatus.Pending
                && p.DecisionDeliveredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DecisionDeliveredAt, now), ct);
    }

    /// <summary>
    /// Move a proposal out of Pending, compared against Pending so that exactly
    /// one decision ever lands on it. Returns the rows written: 0 means it was
    /// no longer pending, or no longer there.
    /// </summary>
    private Task<int> DecideAsync(Guid proposalId, WorkItemEditProposalStatus to, string? rejectionReason, DateTime now, CancellationToken ct)
        => _db.WorkItemEditProposals
            .Where(p => p.Id == proposalId && p.Status == WorkItemEditProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, to)
                .SetProperty(p => p.RejectionReason, rejectionReason)
                .SetProperty(p => p.DecidedAt, now), ct);

    /// <summary>A decision that did not land: the proposal was decided first, or has gone.</summary>
    private async Task<EditProposalDecisionResult> RefusedAsync(string workItemId, Guid proposalId, CancellationToken ct)
    {
        var current = await ReadAsync(workItemId, proposalId, ct);
        return current is null
            ? NotFound()
            : new EditProposalDecisionResult(EditProposalDecisionOutcome.NotPending, current, null);
    }

    private static EditProposalDecisionResult NotFound() => new(EditProposalDecisionOutcome.NotFound, null, null);

    private async Task<WorkItemEditProposal?> FindAsync(string workItemId, Guid proposalId, CancellationToken ct)
    {
        var key = await WorkItemRows.ResolveKeyAsync(_db, workItemId, ct);
        if (key is null) return null;
        return await _db.WorkItemEditProposals.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == proposalId && p.WorkItemId == key.Value, ct);
    }

    private async Task<WorkItemEditProposalDto?> ReadAsync(string workItemId, Guid proposalId, CancellationToken ct)
    {
        var proposal = await FindAsync(workItemId, proposalId, ct);
        return proposal is null ? null : ToDto(proposal, workItemId);
    }

    private static WorkItemEditProposalDto ToDto(WorkItemEditProposal p, string workItemId) => new()
    {
        Id = p.Id,
        WorkItemId = workItemId,
        Status = p.Status,
        Proposed = new EditProposalFieldsDto
        {
            Title = p.ProposedTitle,
            Description = p.ProposedDescription,
            Tags = p.ProposedTagsJson is null ? null : WorkItemMapper.DeserializeTags(p.ProposedTagsJson),
            BranchNameOverride = p.ProposedBranchNameOverride,
            BaseBranchOverride = p.ProposedBaseBranchOverride,
        },
        Snapshot = new EditProposalFieldsDto
        {
            Title = p.SnapshotTitle,
            Description = p.SnapshotDescription,
            Tags = WorkItemMapper.DeserializeTags(p.SnapshotTagsJson),
            BranchNameOverride = p.SnapshotBranchNameOverride,
            BaseBranchOverride = p.SnapshotBaseBranchOverride,
        },
        Rationale = p.Rationale,
        CreatedByLoopRunId = p.CreatedByLoopRunId,
        CreatedByChatSessionId = p.CreatedByChatSessionId,
        RejectionReason = p.RejectionReason,
        CreatedAt = p.CreatedAt,
        DecidedAt = p.DecidedAt,
    };
}

/// <summary>
/// How many pending proposals each item carries, for the work item reads that
/// show the count. One grouped query for any number of items.
/// </summary>
internal static class PendingEditProposalCounts
{
    public static async Task<IReadOnlyDictionary<int, int>> ReadAsync(
        WorkItemServerDbContext db, IReadOnlyList<int> workItemKeys, CancellationToken ct)
    {
        if (workItemKeys.Count == 0) return new Dictionary<int, int>();
        return await db.WorkItemEditProposals
            .Where(p => workItemKeys.Contains(p.WorkItemId) && p.Status == WorkItemEditProposalStatus.Pending)
            .GroupBy(p => p.WorkItemId)
            .Select(g => new { WorkItemId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WorkItemId, x => x.Count, ct);
    }
}
