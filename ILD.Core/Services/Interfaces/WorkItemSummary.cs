using ILD.Core.Services.Remote;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Lightweight, relationship-aware projection of a work item for agent triage.
/// Carries no conversation and only the raw <see cref="Description"/> (callers
/// truncate it to a short preview), so an agent can orient over a large backlog
/// without pulling every full body. <see cref="BlockedBy"/> lists this item's
/// dependency ids and <see cref="BlocksCount"/> the reverse edges, letting the
/// agent reconstruct the dependency graph from a single list call.
/// </summary>
public sealed record WorkItemSummary(
    string Id,
    string Title,
    string? Description,
    RemoteWorkItemStatus Status,
    RemoteWorkItemPriority Priority,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> BlockedBy,
    int BlocksCount,
    bool IsActionable,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? RepositoryId,
    Guid? CreatedByLoopRunId,
    Guid? CreatedByChatSessionId);

/// <summary>
/// Field a <see cref="IWorkItemManager.ListSummariesAsync"/> result is ordered
/// by. <see cref="Priority"/> sorts highest-first; the timestamp orderings sort
/// most-recent-first.
/// </summary>
public enum WorkItemOrderBy
{
    UpdatedAt,
    CreatedAt,
    Priority,
}

/// <summary>
/// Filter + sort + paging options for the agent triage listing. Mirrors the
/// query surface of the agent <c>list_workitems</c> tool. All filters are
/// combined with AND; <see cref="ActionableOnly"/> keeps only items whose
/// dependencies are all <see cref="RemoteWorkItemStatus.Done"/>.
/// </summary>
public sealed record WorkItemListQuery
{
    public RemoteWorkItemStatus? Status { get; init; }
    public RemoteWorkItemPriority? Priority { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public Guid? RepositoryId { get; init; }
    public Guid? CreatedByLoopRunId { get; init; }
    public bool ActionableOnly { get; init; }
    public WorkItemOrderBy OrderBy { get; init; } = WorkItemOrderBy.UpdatedAt;
    public int Skip { get; init; }
    public int Take { get; init; } = 100;
}

/// <summary>
/// Server-side aggregation of a backlog for agent orientation. Carries no
/// bodies. <see cref="Actionable"/> counts items whose dependencies are all
/// Done (vacuously true for items with no dependencies); <see cref="Blocked"/>
/// is the remainder, so <c>Blocked + Actionable == Total</c>.
/// </summary>
public sealed record BacklogSummary(
    int Total,
    IReadOnlyDictionary<string, int> CountsByStatus,
    IReadOnlyDictionary<string, int> CountsByPriority,
    int Blocked,
    int Actionable);
