using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ILD.WorkItemServer.Services;

public interface IWorkItemService
{
    Task<WorkItemDto> CreateAsync(CreateWorkItemRequest req, CancellationToken ct = default);
    Task<WorkItemDto?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkItemDto>> ListAsync(WorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default);
    Task<WorkItemDto?> UpdateAsync(string id, UpdateWorkItemRequest req, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);

    Task<TransitionResponse> TransitionAsync(string id, TransitionRequest req, CancellationToken ct = default);

    /// <summary>
    /// Place an item where it was dropped on the board, and move it to a new
    /// status with it, in one transaction: see <see cref="ReorderWorkItemRequest"/>.
    /// Only the item is written, unless no number is left between its new
    /// neighbours: then one statement renumbers the whole board in the same
    /// order. The order value is not part of history.
    /// </summary>
    Task<ReorderWorkItemResponse> ReorderAsync(string id, ReorderWorkItemRequest req, CancellationToken ct = default);
    Task<bool> AddDependencyAsync(string id, string dependencyId, CancellationToken ct = default);
    Task<bool> RemoveDependencyAsync(string id, string dependencyId, CancellationToken ct = default);
    Task<IReadOnlyList<string>?> GetDependenciesAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Record a pull request against a work item, keyed by URL: a URL the item
    /// already knows is updated in place rather than duplicated, so a client may
    /// report the same PR as often as it likes (on creation, on merge, or while
    /// reconciling what its runs still hold).
    ///
    /// This is the durable home of a work item's PRs. A run's worktree, branch
    /// and PR snapshot are throwaway ILD-local state, but the PR itself touches
    /// the repository and belongs to the item — it has to outlive the run, and
    /// the ILD instance (WI-203).
    /// </summary>
    /// <returns>
    /// Which of the outcomes happened — the three ways this can fail are worth
    /// telling apart, since only one of them means the client should stop
    /// asking. See <see cref="RecordPullRequestOutcome"/>.
    /// </returns>
    Task<RecordPullRequestOutcome> RecordPullRequestAsync(string id, RecordPullRequestRequest req, CancellationToken ct = default);

    Task<PollResponse> PollAsync(IReadOnlyList<string> activeIds, CancellationToken ct = default);
    Task<int> ReclaimStaleAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Promote every <see cref="WorkItemStatus.WorkQueue"/> item whose
    /// dependencies are all satisfied to <see cref="WorkItemStatus.Ready"/>.
    /// A reconciliation backstop for the event-driven
    /// <c>PromoteReadyDependents</c> path: it recovers items that entered the
    /// work queue with their dependencies already complete, or whose promotion
    /// was lost to a crash, cancellation, or concurrent completion.
    /// </summary>
    Task<int> ReconcileWorkQueueAsync(CancellationToken ct = default);

    /// <summary>
    /// The item's history, newest first, each entry paired with the value it
    /// replaced. A deleted item's history stays readable; null only when there is
    /// neither history nor an item with this id.
    /// </summary>
    Task<IReadOnlyList<WorkItemHistoryEntryDto>?> GetHistoryAsync(string id, CancellationToken ct = default);
}

public sealed class WorkItemService : IWorkItemService
{
    private readonly WorkItemServerDbContext _db;
    private readonly TimeProvider _clock;

    public WorkItemService(WorkItemServerDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<WorkItemDto> CreateAsync(CreateWorkItemRequest req, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var w = new WorkItem
        {
            Title = req.Title,
            Description = req.Description,
            CreatedBy = req.CreatedBy,
            CreatedAt = now,
            UpdatedAt = now,
            Status = req.ForceStatus ?? WorkItemStatus.Backlog,
            CreatedByLoopRunId = req.CreatedByLoopRunId,
            CreatedByChatSessionId = req.CreatedByChatSessionId,
            RepositoryId = req.RepositoryId,
            BranchNameOverride = WorkItemMapper.NormalizeBranchRef(req.BranchNameOverride),
            BaseBranchOverride = WorkItemMapper.NormalizeBranchRef(req.BaseBranchOverride),
        };
        WorkItemMapper.WriteTags(w, req.Tags ?? Array.Empty<string>());
        WorkItemMapper.WriteDependencies(w, req.Dependencies ?? Array.Empty<string>());

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var topmost = await _db.WorkItems.MinAsync(x => (double?)x.Priority, ct);
        w.Priority = (topmost ?? 1) - 1;
        _db.WorkItems.Add(w);
        await _db.SaveChangesAsync(ct);
        w.Id = w.InternalId.ToString(CultureInfo.InvariantCulture);
        _db.AddHistorySave(w.Id, WorkItemHistoryKind.Created, WorkItemHistoryFields.Of(_db.Entry(w).CurrentValues), reason: null);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return WorkItemMapper.ToDto(w);
    }

    public async Task<WorkItemDto?> GetAsync(string id, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        return w == null ? null : (await WithDetailsAsync(new[] { w }, ct))[0];
    }

    /// <summary>
    /// The items as a read answers them: two extra projected queries for any
    /// number of items, for the attachment metadata (never the bytes) and the
    /// pending edit proposal counts. Deliberately not used by
    /// <see cref="PollAsync"/>, whose heartbeat carries every active and ready
    /// item and must stay bodiless.
    /// </summary>
    private async Task<IReadOnlyList<WorkItemDto>> WithDetailsAsync(IReadOnlyList<WorkItem> items, CancellationToken ct)
    {
        var keys = items.Select(w => w.InternalId).ToList();
        var attachments = await AttachmentMetadata.ReadAsync(_db, keys, ct);
        var pendingProposals = await PendingEditProposalCounts.ReadAsync(_db, keys, ct);
        return items.Select(w =>
        {
            var dto = WorkItemMapper.ToDto(w, attachments[w.InternalId].ToList());
            dto.PendingEditProposalCount = pendingProposals.GetValueOrDefault(w.InternalId);
            return dto;
        }).ToList();
    }

    public async Task<IReadOnlyList<WorkItemDto>> ListAsync(WorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        IQueryable<WorkItem> q = _db.WorkItems;
        if (status.HasValue) q = q.Where(w => w.Status == status.Value);
        var items = await InBoardOrder(q).ToListAsync(ct);

        if (tags is { Count: > 0 })
        {
            var wanted = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items.Where(w =>
            {
                var t = WorkItemMapper.ReadTags(w);
                return t.Any(x => wanted.Contains(x));
            }).ToList();
        }

        return await WithDetailsAsync(items, ct);
    }

    /// <summary>
    /// Lowest priority first, equal priorities in id order: an item's id is its
    /// InternalId as text, so InternalId orders ids numerically. Every reader
    /// sharing the board sees the same order. This is the board order's source of
    /// truth: the renumber below, ILD's WorkItemOrder and the taskboard's
    /// compareServerOrder follow it.
    /// </summary>
    private static IQueryable<WorkItem> InBoardOrder(IQueryable<WorkItem> items)
        => items.OrderBy(w => w.Priority).ThenBy(w => w.InternalId);

    private static IQueryable<WorkItem> InReverseBoardOrder(IQueryable<WorkItem> items)
        => items.OrderByDescending(w => w.Priority).ThenByDescending(w => w.InternalId);

    public async Task<WorkItemDto?> UpdateAsync(string id, UpdateWorkItemRequest req, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return null;

        if (req.Title != null) w.Title = req.Title;
        if (req.Description != null) w.Description = req.Description;
        if (req.Tags != null) WorkItemMapper.WriteTags(w, req.Tags);
        if (req.Priority != null) w.Priority = req.Priority.Value;
        // Mode and target travel as a unit: setting the mode also sets the
        // target (null clears it). None is a real, settable value — clearing an
        // override back to "no override" must persist, so this cannot key off a
        // truthy mode.
        if (req.AiProviderOverride != null)
        {
            w.AiProviderOverride = req.AiProviderOverride.Value;
            w.AiProviderOverrideId = req.AiProviderOverrideId;
        }
        // Null leaves the custom branch name alone; a blank string clears it.
        // Editing it is how a human resolves a branch conflict, and it only ever
        // affects the item's next run — no run's branch is retroactively renamed.
        if (req.BranchNameOverride != null)
            w.BranchNameOverride = WorkItemMapper.NormalizeBranchRef(req.BranchNameOverride);
        // Same convention for the base ref, and the same reason it is not
        // retroactive: a run pins the base it started from, so an edit only
        // redirects the item's next run.
        if (req.BaseBranchOverride != null)
            w.BaseBranchOverride = WorkItemMapper.NormalizeBranchRef(req.BaseBranchOverride);
        // Not retroactive either: a run pins the repository it was created on.
        if (req.RepositoryId != null) w.RepositoryId = req.RepositoryId;
        w.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return (await WithDetailsAsync(new[] { w }, ct))[0];
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return false;
        _db.WorkItems.Remove(w);
        await _db.SaveChangesAsync(ct);

        // Scrub the deleted id from every other item's dependency list. A
        // dangling reference would otherwise wedge a dependent forever: a
        // Running claim requires every dependency row to exist and be Done
        // (ClaimRunningAsync), so a pointer to a now-missing item is permanently
        // unsatisfiable and never gets cleaned up.
        var dependents = (await _db.WorkItems.ToListAsync(ct))
            .Where(x => WorkItemMapper.ReadDependencies(x).Contains(id))
            .ToList();
        if (dependents.Count > 0)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var d in dependents)
            {
                var deps = WorkItemMapper.ReadDependencies(d).Where(depId => depId != id).ToList();
                WorkItemMapper.WriteDependencies(d, deps);
                d.UpdatedAt = now;
                _db.SetHistoryReason(d, $"dependency {id} deleted");
            }
            await _db.SaveChangesAsync(ct);

            // Dropping the dependency may have satisfied a WorkQueue dependent's
            // last outstanding requirement; promote any that are now ready.
            await PromoteWorkQueueItemsAsync(
                dependents.Where(d => d.Status == WorkItemStatus.WorkQueue).ToList(), $"dependency {id} deleted", ct);
        }

        return true;
    }

    public async Task<TransitionResponse> TransitionAsync(string id, TransitionRequest req, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null)
            return new TransitionResponse { Success = false, ActualStatus = WorkItemStatus.Backlog, Reason = "Not found" };
        return await ApplyTransitionAsync(w, req, ct);
    }

    private async Task<TransitionResponse> ApplyTransitionAsync(WorkItem w, TransitionRequest req, CancellationToken ct)
    {
        // Running is the only validated transition: dependency check + atomic claim.
        if (req.TargetStatus == WorkItemStatus.Running)
            return await ClaimRunningAsync(w, req.Reason, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        w.Status = req.TargetStatus;
        w.UpdatedAt = now;

        if (req.TargetStatus == WorkItemStatus.HumanFeedback && req.Actions != null)
            w.HumanFeedbackActions = req.Actions;
        else if (req.TargetStatus != WorkItemStatus.HumanFeedback)
            w.HumanFeedbackActions = null;

        if (req.TargetStatus == WorkItemStatus.Done || req.TargetStatus == WorkItemStatus.Ready)
            w.LastHeartbeatAt = null;

        _db.SetHistoryReason(w, req.Reason);
        await _db.SaveChangesAsync(ct);

        // Once an item is Done, any WorkQueue item that was waiting on it may now
        // have all its dependencies satisfied. Promote those to Ready so the
        // scheduler picks them up on its next poll, instead of waiting for a
        // human nudge or the stale-reclaim sweep. Done is persisted above first
        // so the readiness check below reads the up-to-date dependency status.
        if (req.TargetStatus == WorkItemStatus.Done)
            await PromoteReadyDependentsAsync(w.Id, ct);

        return new TransitionResponse { Success = true, ActualStatus = w.Status };
    }

    /// <summary>
    /// Promote every <see cref="WorkItemStatus.WorkQueue"/> item whose
    /// dependencies are now all <see cref="WorkItemStatus.Done"/> to
    /// <see cref="WorkItemStatus.Ready"/>. Called after a work item reaches Done
    /// so its dependents auto-flow into the run queue. Backlog dependents are
    /// deliberately left untouched — they still require human approval to enter
    /// the work queue.
    /// </summary>
    private async Task PromoteReadyDependentsAsync(string completedId, CancellationToken ct)
    {
        var dependents = (await _db.WorkItems
                .Where(w => w.Status == WorkItemStatus.WorkQueue)
                .ToListAsync(ct))
            .Where(w => WorkItemMapper.ReadDependencies(w).Contains(completedId))
            .ToList();
        await PromoteWorkQueueItemsAsync(dependents, $"dependency {completedId} done", ct);
    }

    /// <inheritdoc />
    public async Task<int> ReconcileWorkQueueAsync(CancellationToken ct = default)
    {
        var candidates = await _db.WorkItems
            .Where(w => w.Status == WorkItemStatus.WorkQueue)
            .ToListAsync(ct);
        return await PromoteWorkQueueItemsAsync(candidates, "dependencies done", ct);
    }

    /// <summary>
    /// Promote each supplied <see cref="WorkItemStatus.WorkQueue"/> candidate
    /// whose dependencies are all <see cref="WorkItemStatus.Done"/> (an item
    /// with no dependencies is trivially satisfied) to
    /// <see cref="WorkItemStatus.Ready"/>. Returns the number promoted.
    /// Backlog dependents are never passed in by callers — they still require
    /// human approval to enter the work queue. <paramref name="reason"/> goes on
    /// each promotion's history.
    /// </summary>
    private async Task<int> PromoteWorkQueueItemsAsync(IReadOnlyList<WorkItem> candidates, string reason, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;

        var depIds = candidates
            .SelectMany(WorkItemMapper.ReadDependencies)
            .Distinct()
            .ToList();
        var statusById = await _db.WorkItems
            .Where(w => depIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Status })
            .ToDictionaryAsync(x => x.Id, x => x.Status, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var promoted = 0;
        foreach (var w in candidates)
        {
            var allDone = WorkItemMapper.ReadDependencies(w)
                .All(id => statusById.TryGetValue(id, out var s) && s == WorkItemStatus.Done);
            if (!allDone) continue;
            w.Status = WorkItemStatus.Ready;
            w.UpdatedAt = now;
            w.LastHeartbeatAt = null;
            _db.SetHistoryReason(w, reason);
            promoted++;
        }

        if (promoted > 0)
            await _db.SaveChangesAsync(ct);
        return promoted;
    }

    /// <summary>
    /// Claim an item into <see cref="WorkItemStatus.Running"/> for exactly one
    /// caller. The claim is a single conditional UPDATE (<c>WHERE Id == id AND
    /// Status != Running</c>): when two clients read the same item as unclaimed
    /// and both try to claim, the database serializes the writes so only the
    /// first flips a row — every later attempt updates zero rows and is rejected
    /// with "Already claimed". This is the atomicity the read-then-write guard
    /// could not provide. A won claim writes its status history in the same
    /// transaction; a lost one writes none.
    /// </summary>
    private async Task<TransitionResponse> ClaimRunningAsync(WorkItem w, string? reason, CancellationToken ct)
    {
        var depIds = WorkItemMapper.ReadDependencies(w);
        if (depIds.Count > 0)
        {
            var deps = await _db.WorkItems
                .Where(x => depIds.Contains(x.Id))
                .Select(x => x.Status)
                .ToListAsync(ct);
            if (deps.Count != depIds.Count || deps.Any(s => s != WorkItemStatus.Done))
            {
                return new TransitionResponse
                {
                    Success = false,
                    ActualStatus = w.Status,
                    Reason = "Dependencies not satisfied",
                };
            }
        }

        // Running bumps the heartbeat so the stale detector treats the claim as
        // fresh from the very first poll, and clears any prior HumanFeedback
        // actions — mirroring the shared transition path for non-Running states.
        var now = _clock.GetUtcNow().UtcDateTime;
        // A claim made as part of a reorder joins the reorder's transaction.
        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;
        var claimed = await _db.WorkItems
            .Where(x => x.Id == w.Id && x.Status != WorkItemStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, WorkItemStatus.Running)
                .SetProperty(x => x.UpdatedAt, now)
                .SetProperty(x => x.LastHeartbeatAt, now)
                .SetProperty(x => x.HumanFeedbackActions, (string?)null), ct);

        if (claimed == 0)
        {
            return new TransitionResponse
            {
                Success = false,
                ActualStatus = WorkItemStatus.Running,
                Reason = "Already claimed",
            };
        }

        _db.AddHistorySave(w.Id, WorkItemHistoryKind.Changed,
            [(WorkItemHistoryFields.Status, WorkItemHistoryFields.Encode(WorkItemStatus.Running))], reason);
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);

        // The conditional UPDATE bypasses the change tracker, so the entity we
        // loaded above still holds its pre-claim state. Reload it from the row
        // we just wrote so any further use of this context sees the claim.
        await _db.Entry(w).ReloadAsync(ct);
        return new TransitionResponse { Success = true, ActualStatus = WorkItemStatus.Running };
    }

    public async Task<ReorderWorkItemResponse> ReorderAsync(string id, ReorderWorkItemRequest req, CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return new ReorderWorkItemResponse { Outcome = ReorderWorkItemOutcome.NotFound };

        var above = req.AboveId is null ? null : await BoardKeyOfAsync(req.AboveId, ct);
        var below = req.BelowId is null ? null : await BoardKeyOfAsync(req.BelowId, ct);
        if ((req.AboveId is null && req.BelowId is null)
            || req.AboveId == id || req.BelowId == id
            || (req.AboveId is not null && above is null)
            || (req.BelowId is not null && below is null))
            return new ReorderWorkItemResponse { Outcome = ReorderWorkItemOutcome.InvalidNeighbour };

        if (req.TargetStatus is { } target && target != w.Status)
        {
            var moved = await ApplyTransitionAsync(w, new TransitionRequest
            {
                TargetStatus = target,
                Actions = req.Actions,
                Reason = req.Reason,
            }, ct);
            if (!moved.Success)
                return new ReorderWorkItemResponse { Outcome = ReorderWorkItemOutcome.TransitionRefused, Reason = moved.Reason };
        }

        var others = _db.WorkItems.Where(x => x.InternalId != w.InternalId);
        BoardKey? prev, next;
        if (above is not null)
        {
            prev = above;
            next = await InBoardOrder(others.Where(x => x.Priority > above.Priority
                    || (x.Priority == above.Priority && x.InternalId > above.InternalId)))
                .Select(x => new BoardKey(x.InternalId, x.Priority))
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            next = below!;
            prev = await InReverseBoardOrder(others.Where(x => x.Priority < next.Priority
                    || (x.Priority == next.Priority && x.InternalId < next.InternalId)))
                .Select(x => new BoardKey(x.InternalId, x.Priority))
                .FirstOrDefaultAsync(ct);
        }
        var priority = (prev, next) switch
        {
            (not null, not null) => prev.Priority + (next.Priority - prev.Priority) / 2,
            (not null, null) => prev.Priority + 1,
            _ => next!.Priority - 1,
        };
        var fits = (prev is null || prev.Priority < priority) && (next is null || priority < next.Priority);

        if (fits) w.Priority = priority;
        w.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        if (!fits)
        {
            await RenumberAsync(w.InternalId, (above ?? below)!.InternalId, above is not null ? 1 : -1, ct);
            await _db.Entry(w).ReloadAsync(ct);
        }
        await transaction.CommitAsync(ct);

        return new ReorderWorkItemResponse
        {
            Outcome = ReorderWorkItemOutcome.Moved,
            Item = (await WithDetailsAsync(new[] { w }, ct))[0],
            Renumbered = !fits,
        };
    }

    /// <summary>An item's place in <see cref="InBoardOrder"/>.</summary>
    private sealed record BoardKey(int InternalId, double Priority);

    private Task<BoardKey?> BoardKeyOfAsync(string id, CancellationToken ct)
        => _db.WorkItems.Where(x => x.Id == id)
            .Select(x => new BoardKey(x.InternalId, x.Priority))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Every item but the moved one gets twice its rank in <see cref="InBoardOrder"/>,
    /// and the moved one the odd number directly after (<paramref name="side"/> 1)
    /// or before (-1) its neighbour's: the same order with room between every
    /// two items, written in one statement.
    /// </summary>
    private Task RenumberAsync(int moved, int neighbour, int side, CancellationToken ct)
        => _db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH others AS (
                SELECT "InternalId", 2 * (ROW_NUMBER() OVER (ORDER BY "Priority", "InternalId") - 1) AS "Value"
                FROM "WorkItems"
                WHERE "InternalId" <> {moved}),
            placed AS (
                SELECT "InternalId", "Value" FROM others
                UNION ALL
                SELECT {moved}, "Value" + {side} FROM others WHERE "InternalId" = {neighbour})
            UPDATE "WorkItems" SET "Priority" = placed."Value"
            FROM placed
            WHERE "WorkItems"."InternalId" = placed."InternalId"
            """, ct);

    public async Task<bool> AddDependencyAsync(string id, string dependencyId, CancellationToken ct = default)
    {
        if (id == dependencyId) return false;
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return false;
        var depExists = await _db.WorkItems.AnyAsync(x => x.Id == dependencyId, ct);
        if (!depExists) return false;

        var deps = WorkItemMapper.ReadDependencies(w).ToList();
        if (deps.Contains(dependencyId)) return true;
        deps.Add(dependencyId);
        WorkItemMapper.WriteDependencies(w, deps);
        w.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveDependencyAsync(string id, string dependencyId, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return false;
        var deps = WorkItemMapper.ReadDependencies(w).ToList();
        if (!deps.Remove(dependencyId)) return false;
        WorkItemMapper.WriteDependencies(w, deps);
        w.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<string>?> GetDependenciesAsync(string id, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        return w == null ? null : WorkItemMapper.ReadDependencies(w);
    }

    /// <summary>
    /// How many times a losing writer re-reads and re-applies its report before
    /// giving up. Contention is between a handful of reporters at most, so a
    /// couple of retries covers it; a caller that still loses reports the same
    /// PR again on its next pass.
    /// </summary>
    private const int RecordPullRequestAttempts = 3;

    public async Task<RecordPullRequestOutcome> RecordPullRequestAsync(string id, RecordPullRequestRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Url)) return RecordPullRequestOutcome.InvalidRequest;

        // Recording a PR is a read-modify-write of the whole list, so it is done
        // as a compare-and-swap against the column it read: two reporters
        // arriving together (a PR node opening one while a webhook marks another
        // merged, or two ILD instances reconciling the same item) would
        // otherwise silently drop one of the two updates — and losing a PR link
        // is the entire failure this record exists to prevent. The loser re-runs
        // its own decision against the winner's list, which is why a retry is
        // always safe here: every field is add-only.
        for (var attempt = 0; attempt < RecordPullRequestAttempts; attempt++)
        {
            var w = await _db.WorkItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (w == null) return RecordPullRequestOutcome.NotFound;

            var seen = w.PullRequestsJson;
            var now = _clock.GetUtcNow().UtcDateTime;
            var prs = WorkItemMapper.ReadPullRequests(w);
            var existing = prs.FindIndex(p => string.Equals(p.Url, req.Url, StringComparison.Ordinal));
            string? historyEvent;

            if (existing >= 0)
            {
                var was = prs[existing];
                // Merge is one-way and the created time is the earliest
                // sighting: re-reporting a PR can add to what the item knows,
                // never subtract.
                var updated = was with
                {
                    LoopRunId = req.LoopRunId ?? was.LoopRunId,
                    Merged = was.Merged || req.Merged,
                    CreatedAt = req.CreatedAt is { } at && at < was.CreatedAt ? at : was.CreatedAt,
                };
                // Already known, exactly as reported — the common case on the
                // read path, and no reason to touch the row.
                if (updated == was) return RecordPullRequestOutcome.Recorded;
                prs[existing] = updated;
                historyEvent = updated.Merged && !was.Merged ? WorkItemHistoryFields.PullRequestMerged : null;
            }
            else
            {
                prs.Add(new WorkItemPullRequest(req.Url, req.LoopRunId, req.Merged, req.CreatedAt ?? now));
                historyEvent = req.Merged ? WorkItemHistoryFields.PullRequestMerged : WorkItemHistoryFields.PullRequestLinked;
            }

            var json = WorkItemMapper.SerializePullRequests(prs);
            // Status is deliberately untouched — linking a PR is a record of
            // what the item produced, not a lifecycle transition. UpdatedAt
            // moves so clients ordering by it still see the change.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var rows = await _db.WorkItems
                .Where(x => x.Id == id && x.PullRequestsJson == seen)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.PullRequestsJson, json)
                    .SetProperty(x => x.UpdatedAt, now), ct);
            if (rows == 0) continue;

            if (historyEvent is not null)
            {
                _db.AddHistorySave(id, WorkItemHistoryKind.Changed, [(historyEvent, req.Url)], reason: null);
                await _db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);

            // The write went straight to the column, so a copy this scope was
            // already tracking still holds the old list — refresh it, or a
            // later read in the same request would answer from it.
            var tracked = _db.ChangeTracker.Entries<WorkItem>().FirstOrDefault(e => e.Entity.Id == id);
            if (tracked is not null) await tracked.ReloadAsync(ct);
            return RecordPullRequestOutcome.Recorded;
        }

        return RecordPullRequestOutcome.Conflict;
    }

    public async Task<PollResponse> PollAsync(IReadOnlyList<string> activeIds, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var active = activeIds.Count == 0
            ? new List<WorkItem>()
            : await _db.WorkItems.Where(w => activeIds.Contains(w.Id)).ToListAsync(ct);

        // Heartbeat: every active item the caller still considers theirs gets
        // its LastHeartbeatAt refreshed.
        foreach (var w in active)
            w.LastHeartbeatAt = now;

        var ready = await InBoardOrder(_db.WorkItems.Where(w => w.Status == WorkItemStatus.Ready))
            .ToListAsync(ct);

        if (active.Count > 0)
            await _db.SaveChangesAsync(ct);

        return new PollResponse
        {
            ActiveItems = active.Select(WorkItemMapper.ToDto).ToList(),
            ReadyItems = ready.Select(WorkItemMapper.ToDto).ToList(),
        };
    }

    public async Task<int> ReclaimStaleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - timeout;
        var stale = await _db.WorkItems
            .Where(w => (w.Status == WorkItemStatus.Running
                      || w.Status == WorkItemStatus.WaitingForIld)
                    && w.LastHeartbeatAt != null
                    && w.LastHeartbeatAt < cutoff)
            .ToListAsync(ct);

        foreach (var w in stale)
        {
            _db.SetHistoryReason(w, $"no heartbeat since {w.LastHeartbeatAt:u}");
            w.Status = WorkItemStatus.Ready;
            w.LastHeartbeatAt = null;
            w.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }
        if (stale.Count > 0)
            await _db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task<IReadOnlyList<WorkItemHistoryEntryDto>?> GetHistoryAsync(string id, CancellationToken ct = default)
    {
        // Ordered by Id, the order the rows were written in: a timestamp is taken
        // before its write lands, so two writers can stamp out of order.
        var rows = await _db.WorkItemHistoryEntries.AsNoTracking()
            .Where(e => e.WorkItemId == id)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
        if (rows.Count == 0 && !await _db.WorkItems.AnyAsync(w => w.Id == id, ct)) return null;

        var latest = new Dictionary<string, string?>(StringComparer.Ordinal);
        var history = new List<WorkItemHistoryEntryDto>(rows.Count);
        foreach (var row in rows)
        {
            string? old = null;
            if (!WorkItemHistoryFields.IsEvent(row.Field))
            {
                latest.TryGetValue(row.Field, out old);
                latest[row.Field] = row.Value;
            }
            history.Add(new WorkItemHistoryEntryDto
            {
                SaveId = row.SaveId,
                Timestamp = row.Timestamp,
                Kind = row.Kind,
                Username = row.Username,
                ActorKind = row.ActorKind,
                ActorDetail = row.ActorDetail,
                ActorWorkItemId = row.ActorWorkItemId,
                Field = row.Field,
                OldValue = old,
                NewValue = row.Value,
                Reason = row.Reason,
            });
        }
        history.Reverse();
        return history;
    }
}
