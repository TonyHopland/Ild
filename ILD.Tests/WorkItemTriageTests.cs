using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer.Dtos;
using Microsoft.EntityFrameworkCore;
using Moq;
using WiPriority = ILD.WorkItemServer.Domain.WorkItemPriority;
using WiStatus = ILD.WorkItemServer.Domain.WorkItemStatus;

namespace ILD.Tests;

/// <summary>
/// Covers the agent triage surface on <see cref="WorkItemManager"/>:
/// the lightweight relationship-aware listing (<c>ListSummariesAsync</c>) with
/// its filters/sort, and the backlog aggregation (<c>GetBacklogSummaryAsync</c>).
/// Items are seeded straight through the in-memory WorkItem server so priority,
/// status, tags, dependencies, and timestamps can all be controlled exactly.
/// </summary>
public sealed class WorkItemTriageTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly WorkItemManager _mgr;

    public WorkItemTriageTests()
    {
        _mgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object,
            _db.Providers,
            new Mock<IEventLogService>().Object,
            _db.LoopRuns,
            _db.ServerClient,
            _db.ServerOptions);
    }

    public void Dispose() => _db.Dispose();

    private async Task<string> SeedAsync(
        string title,
        WiPriority priority = WiPriority.Medium,
        WiStatus status = WiStatus.Backlog,
        string[]? tags = null,
        string[]? deps = null,
        string? description = null,
        Guid? repositoryId = null,
        Guid? createdByLoopRunId = null,
        DateTime? timestamp = null)
    {
        var dto = await _db.Server.Service.CreateAsync(new CreateWorkItemRequest
        {
            Title = title,
            Description = description,
            Priority = priority,
            Tags = tags ?? Array.Empty<string>(),
            Dependencies = deps ?? Array.Empty<string>(),
            ForceStatus = status,
            RepositoryId = repositoryId,
            CreatedByLoopRunId = createdByLoopRunId,
        });

        if (timestamp.HasValue)
        {
            var entity = await _db.Server.ServerDb.WorkItems.FirstAsync(w => w.Id == dto.Id);
            entity.CreatedAt = timestamp.Value;
            entity.UpdatedAt = timestamp.Value;
            await _db.Server.ServerDb.SaveChangesAsync();
        }
        return dto.Id;
    }

    [Fact]
    public async Task ListSummaries_exposes_dependency_edges_and_actionable_flag()
    {
        var done = await SeedAsync("done dep", status: WiStatus.Done);
        var pending = await SeedAsync("pending dep", status: WiStatus.Backlog);
        var blocked = await SeedAsync("blocked", deps: new[] { done, pending });
        var ready = await SeedAsync("ready", deps: new[] { done });

        var rows = await _mgr.ListSummariesAsync(new WorkItemListQuery());

        var blockedRow = rows.Single(r => r.Id == blocked);
        Assert.Equal(new[] { done, pending }, blockedRow.BlockedBy);
        Assert.Equal(2, blockedRow.BlockedBy.Count);
        Assert.Equal(0, blockedRow.BlocksCount);
        Assert.False(blockedRow.IsActionable); // one dependency is not Done

        var readyRow = rows.Single(r => r.Id == ready);
        Assert.True(readyRow.IsActionable); // its only dependency is Done

        // Reverse edges: `done` is depended on by both `blocked` and `ready`.
        Assert.Equal(2, rows.Single(r => r.Id == done).BlocksCount);
        Assert.Equal(1, rows.Single(r => r.Id == pending).BlocksCount);

        // An item with no dependencies is vacuously actionable.
        Assert.True(rows.Single(r => r.Id == done).IsActionable);
    }

    [Fact]
    public async Task ListSummaries_actionableOnly_keeps_only_items_with_all_dependencies_done()
    {
        var done = await SeedAsync("done", status: WiStatus.Done);
        var standalone = await SeedAsync("standalone");
        var ready = await SeedAsync("ready", deps: new[] { done });
        var blocked = await SeedAsync("blocked", deps: new[] { standalone });

        var rows = await _mgr.ListSummariesAsync(new WorkItemListQuery { ActionableOnly = true });
        var ids = rows.Select(r => r.Id).ToHashSet();

        Assert.Contains(done, ids);
        Assert.Contains(standalone, ids);
        Assert.Contains(ready, ids);
        Assert.DoesNotContain(blocked, ids); // `standalone` dependency is not Done
    }

    [Fact]
    public async Task ListSummaries_actionableOnly_includes_items_with_zero_dependencies()
    {
        // A dependency-free item must never be filtered out by actionableOnly — its
        // (empty) dependency set is vacuously all-Done. This guards against the
        // classic INNER-JOIN mistake that would drop items with no dependency rows.
        var noDeps = await SeedAsync("no deps");

        var rows = await _mgr.ListSummariesAsync(new WorkItemListQuery { ActionableOnly = true });

        var row = rows.Single(r => r.Id == noDeps);
        Assert.True(row.IsActionable);
        Assert.Empty(row.BlockedBy);
    }

    [Fact]
    public async Task ListSummaries_treats_missing_dependency_as_blocking()
    {
        // A dependency id with no matching item must keep the dependent blocked,
        // mirroring the server's "every dep present and Done" readiness rule.
        var orphan = await SeedAsync("orphan", deps: new[] { "does-not-exist" });

        var actionable = await _mgr.ListSummariesAsync(new WorkItemListQuery { ActionableOnly = true });
        Assert.DoesNotContain(orphan, actionable.Select(r => r.Id));

        var row = (await _mgr.ListSummariesAsync(new WorkItemListQuery())).Single(r => r.Id == orphan);
        Assert.False(row.IsActionable);
    }

    [Fact]
    public async Task ListSummaries_filters_by_priority_and_tags()
    {
        await SeedAsync("low", priority: WiPriority.Low, tags: new[] { "backend" });
        var highBackend = await SeedAsync("high backend", priority: WiPriority.High, tags: new[] { "backend" });
        await SeedAsync("high frontend", priority: WiPriority.High, tags: new[] { "frontend" });

        var byPriority = await _mgr.ListSummariesAsync(new WorkItemListQuery { Priority = RemoteWorkItemPriority.High });
        Assert.Equal(2, byPriority.Count);
        Assert.All(byPriority, r => Assert.Equal(RemoteWorkItemPriority.High, r.Priority));

        var byTag = await _mgr.ListSummariesAsync(new WorkItemListQuery
        {
            Priority = RemoteWorkItemPriority.High,
            Tags = new[] { "backend" },
        });
        Assert.Equal(highBackend, Assert.Single(byTag).Id);
    }

    [Fact]
    public async Task ListSummaries_orders_by_priority_highest_first()
    {
        await SeedAsync("medium", priority: WiPriority.Medium);
        await SeedAsync("critical", priority: WiPriority.Critical);
        await SeedAsync("low", priority: WiPriority.Low);
        await SeedAsync("high", priority: WiPriority.High);

        var rows = await _mgr.ListSummariesAsync(new WorkItemListQuery { OrderBy = WorkItemOrderBy.Priority });

        Assert.Equal(
            new[] { RemoteWorkItemPriority.Critical, RemoteWorkItemPriority.High, RemoteWorkItemPriority.Medium, RemoteWorkItemPriority.Low },
            rows.Select(r => r.Priority).ToArray());
    }

    [Fact]
    public async Task ListSummaries_orders_by_updatedAt_most_recent_first()
    {
        var old = await SeedAsync("old", timestamp: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = await SeedAsync("new", timestamp: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = await SeedAsync("mid", timestamp: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var rows = await _mgr.ListSummariesAsync(new WorkItemListQuery { OrderBy = WorkItemOrderBy.UpdatedAt });

        Assert.Equal(new[] { newest, middle, old }, rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task ListSummaries_paging_applies_after_sort()
    {
        await SeedAsync("a", timestamp: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = await SeedAsync("b", timestamp: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var c = await SeedAsync("c", timestamp: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var page = await _mgr.ListSummariesAsync(new WorkItemListQuery
        {
            OrderBy = WorkItemOrderBy.CreatedAt,
            Skip = 0,
            Take = 2,
        });

        Assert.Equal(new[] { c, b }, page.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task GetBacklogSummary_aggregates_status_priority_and_blocked_vs_actionable()
    {
        var done = await SeedAsync("done", priority: WiPriority.High, status: WiStatus.Done);
        await SeedAsync("ready", priority: WiPriority.High, status: WiStatus.Backlog, deps: new[] { done });
        await SeedAsync("blocked", priority: WiPriority.Low, status: WiStatus.WorkQueue, deps: new[] { "missing" });

        var summary = await _mgr.GetBacklogSummaryAsync(null);

        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.CountsByStatus["Done"]);
        Assert.Equal(1, summary.CountsByStatus["Backlog"]);
        Assert.Equal(1, summary.CountsByStatus["WorkQueue"]);
        Assert.Equal(2, summary.CountsByPriority["High"]);
        Assert.Equal(1, summary.CountsByPriority["Low"]);
        // `done` (no deps) and `ready` (dep Done) are actionable; `blocked` is not.
        Assert.Equal(2, summary.Actionable);
        Assert.Equal(1, summary.Blocked);
        Assert.Equal(summary.Total, summary.Actionable + summary.Blocked);
    }

    [Fact]
    public async Task GetBacklogSummary_scopes_counts_to_a_repository()
    {
        var repoA = Guid.NewGuid();
        var repoB = Guid.NewGuid();
        await SeedAsync("a1", repositoryId: repoA);
        await SeedAsync("a2", repositoryId: repoA);
        await SeedAsync("b1", repositoryId: repoB);

        var summary = await _mgr.GetBacklogSummaryAsync(repoA);

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.CountsByStatus["Backlog"]);
    }

    [Fact]
    public async Task ListSummaries_filters_by_createdByLoopRunId()
    {
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        await SeedAsync("from A 1", createdByLoopRunId: runA);
        await SeedAsync("from A 2", createdByLoopRunId: runA);
        await SeedAsync("from B", createdByLoopRunId: runB);

        var rows = await _mgr.ListSummariesAsync(new WorkItemListQuery { CreatedByLoopRunId = runA });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(runA, r.CreatedByLoopRunId));
    }
}
