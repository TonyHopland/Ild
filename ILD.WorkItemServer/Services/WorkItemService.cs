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
    Task<bool> AddDependencyAsync(string id, string dependencyId, CancellationToken ct = default);
    Task<bool> RemoveDependencyAsync(string id, string dependencyId, CancellationToken ct = default);
    Task<IReadOnlyList<string>?> GetDependenciesAsync(string id, CancellationToken ct = default);

    Task<bool> AppendFeedbackAsync(string id, string content, CancellationToken ct = default);

    /// <summary>
    /// Append a conversation entry without changing the work item's status.
    /// Used by the engine to record AI-node turns (coder ↔ reviewer ↔ human)
    /// as they happen, so the dialogue can be followed in the UI.
    /// </summary>
    Task<bool> AppendConversationAsync(string id, string role, string content, string? name, CancellationToken ct = default);

    Task<PollResponse> PollAsync(IReadOnlyList<string> activeIds, CancellationToken ct = default);
    Task<int> ReclaimStaleAsync(TimeSpan timeout, CancellationToken ct = default);
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
            Priority = req.Priority,
            Status = req.ForceStatus ?? WorkItemStatus.Backlog,
            CreatedByLoopRunId = req.CreatedByLoopRunId,
            RepositoryId = req.RepositoryId,
        };
        WorkItemMapper.WriteTags(w, req.Tags ?? Array.Empty<string>());
        WorkItemMapper.WriteDependencies(w, req.Dependencies ?? Array.Empty<string>());
        WorkItemMapper.WriteConversation(w, Array.Empty<ConversationMessage>());

        _db.WorkItems.Add(w);
        await _db.SaveChangesAsync(ct);
        w.Id = w.InternalId.ToString(CultureInfo.InvariantCulture);
        await _db.SaveChangesAsync(ct);
        return WorkItemMapper.ToDto(w);
    }

    public async Task<WorkItemDto?> GetAsync(string id, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        return w == null ? null : WorkItemMapper.ToDto(w);
    }

    public async Task<IReadOnlyList<WorkItemDto>> ListAsync(WorkItemStatus? status, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        IQueryable<WorkItem> q = _db.WorkItems;
        if (status.HasValue) q = q.Where(w => w.Status == status.Value);
        var items = await q.ToListAsync(ct);

        if (tags is { Count: > 0 })
        {
            var wanted = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items.Where(w =>
            {
                var t = WorkItemMapper.ReadTags(w);
                return t.Any(x => wanted.Contains(x));
            }).ToList();
        }

        return items.Select(WorkItemMapper.ToDto).ToList();
    }

    public async Task<WorkItemDto?> UpdateAsync(string id, UpdateWorkItemRequest req, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return null;

        if (req.Title != null) w.Title = req.Title;
        if (req.Description != null) w.Description = req.Description;
        if (req.Tags != null) WorkItemMapper.WriteTags(w, req.Tags);
        w.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return WorkItemMapper.ToDto(w);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return false;
        _db.WorkItems.Remove(w);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<TransitionResponse> TransitionAsync(string id, TransitionRequest req, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null)
            return new TransitionResponse { Success = false, ActualStatus = WorkItemStatus.Backlog, Reason = "Not found" };

        // Running is the only validated transition: dependency check + atomic claim.
        if (req.TargetStatus == WorkItemStatus.Running)
            return await ClaimRunningAsync(w, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        w.Status = req.TargetStatus;
        w.UpdatedAt = now;

        if (req.TargetStatus == WorkItemStatus.HumanFeedback && req.Actions != null)
            w.HumanFeedbackActions = req.Actions;
        else if (req.TargetStatus != WorkItemStatus.HumanFeedback)
            w.HumanFeedbackActions = null;

        // Append a conversation entry on transitions to response states when a
        // reason is supplied. Both response states (HumanFeedback, Done) are
        // system/AI-authored events, so the role is "ai"; an optional Name
        // (e.g. the node's title) gives the entry a friendly author label.
        if (req.Reason != null && IsResponseState(req.TargetStatus))
        {
            var msgs = WorkItemMapper.ReadConversation(w);
            msgs.Add(new ConversationMessage(
                Role: "ai",
                Content: req.Reason,
                Timestamp: now,
                Name: req.Name));
            WorkItemMapper.WriteConversation(w, msgs);
        }

        if (req.TargetStatus == WorkItemStatus.Done || req.TargetStatus == WorkItemStatus.Ready)
            w.LastHeartbeatAt = null;

        await _db.SaveChangesAsync(ct);
        return new TransitionResponse { Success = true, ActualStatus = w.Status };
    }

    /// <summary>
    /// Claim an item into <see cref="WorkItemStatus.Running"/> for exactly one
    /// caller. The claim is a single conditional UPDATE (<c>WHERE Id == id AND
    /// Status != Running</c>): when two clients read the same item as unclaimed
    /// and both try to claim, the database serializes the writes so only the
    /// first flips a row — every later attempt updates zero rows and is rejected
    /// with "Already claimed". This is the atomicity the read-then-write guard
    /// could not provide.
    /// </summary>
    private async Task<TransitionResponse> ClaimRunningAsync(WorkItem w, CancellationToken ct)
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

        // The conditional UPDATE bypasses the change tracker, so the entity we
        // loaded above still holds its pre-claim state. Reload it from the row
        // we just wrote so any further use of this context sees the claim.
        await _db.Entry(w).ReloadAsync(ct);
        return new TransitionResponse { Success = true, ActualStatus = WorkItemStatus.Running };
    }

    private static bool IsResponseState(WorkItemStatus s)
        => s == WorkItemStatus.HumanFeedback
        || s == WorkItemStatus.Done;

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

    public async Task<bool> AppendFeedbackAsync(string id, string content, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return false;
        var now = _clock.GetUtcNow().UtcDateTime;
        var msgs = WorkItemMapper.ReadConversation(w);
        msgs.Add(new ConversationMessage("human", content, now));
        WorkItemMapper.WriteConversation(w, msgs);
        // Per PRD: human feedback transitions the item to WaitingForIld so the
        // claiming ILD instance picks it back up on its next poll.
        w.Status = WorkItemStatus.WaitingForIld;
        w.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AppendConversationAsync(string id, string role, string content, string? name, CancellationToken ct = default)
    {
        var w = await _db.WorkItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w == null) return false;
        var now = _clock.GetUtcNow().UtcDateTime;
        var msgs = WorkItemMapper.ReadConversation(w);
        msgs.Add(new ConversationMessage(role, content, now, name));
        WorkItemMapper.WriteConversation(w, msgs);
        // Status is intentionally left untouched — an AI turn is dialogue, not
        // a lifecycle transition.
        w.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
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

        var ready = await _db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Ready)
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
            w.Status = WorkItemStatus.Ready;
            w.LastHeartbeatAt = null;
            w.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }
        if (stale.Count > 0)
            await _db.SaveChangesAsync(ct);
        return stale.Count;
    }
}
