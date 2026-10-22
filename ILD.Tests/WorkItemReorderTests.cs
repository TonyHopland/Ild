using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer.Dtos;
using Moq;
using WiStatus = ILD.WorkItemServer.Domain.WorkItemStatus;

namespace ILD.Tests;

/// <summary>
/// Moving a card on the board: <see cref="WorkItemManager"/> places the item
/// next to the neighbours of the drop spot in the whole board's order (every
/// status), which the WorkItem server works out and writes. Items are
/// seeded straight into the in-memory WorkItem server so every order value is
/// exact; the expected board order is worked out here from the stored values.
/// </summary>
public sealed class WorkItemReorderTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Mock<IWorkItemNotifier> _notifier = new();
    private readonly WorkItemManager _mgr;

    public WorkItemReorderTests()
    {
        _mgr = new WorkItemManager(
            new Mock<IRepositoryManager>().Object,
            _db.Providers,
            new Mock<IEventLogService>().Object,
            _db.LoopRuns,
            _db.ServerClient,
            _db.ServerOptions, TestActors.Instance,
            notifier: _notifier.Object);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SeedAsync(double priority, WiStatus status = WiStatus.Backlog)
    {
        var dto = await _db.Server.Service.CreateAsync(new CreateWorkItemRequest { Title = "w", ForceStatus = status }, Ct);
        await _db.Server.Service.UpdateAsync(dto.Id, new UpdateWorkItemRequest { Priority = priority }, Ct);
        _db.Server.ServerDb.ChangeTracker.Clear();
        return dto.Id;
    }

    private async Task<IReadOnlyList<WorkItemDto>> AllAsync()
    {
        _db.Server.ServerDb.ChangeTracker.Clear();
        return await _db.Server.Service.ListAsync(null, null, Ct);
    }

    /// <summary>The whole board's order by the stated rule: priority, then the id shorter-first, then ordinal.</summary>
    private async Task<IReadOnlyList<string>> BoardAsync()
        => (await AllAsync())
            .OrderBy(w => w.Priority)
            .ThenBy(w => w.Id.Length)
            .ThenBy(w => w.Id, StringComparer.Ordinal)
            .Select(w => w.Id)
            .ToList();

    private void AssertAnnouncedOnly(params (string Id, RemoteWorkItemStatus Status)[] expected)
    {
        foreach (var (id, status) in expected)
            _notifier.Verify(n => n.WorkItemStateChangedAsync(id, status, status), Times.Once);
        _notifier.Verify(n => n.WorkItemStateChangedAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()),
            Times.Exactly(expected.Length));
    }

    [Fact]
    public async Task Dropping_between_two_neighbours_places_the_item_there_and_writes_only_that_item()
    {
        var a = await SeedAsync(0);
        var b = await SeedAsync(1);
        var c = await SeedAsync(2);
        var before = (await AllAsync()).ToDictionary(w => w.Id);

        await _mgr.ReorderAsync(TestActors.Human, c, aboveId: a, belowId: b);

        Assert.Equal(new[] { a, c, b }, await BoardAsync());
        var after = (await AllAsync()).ToDictionary(w => w.Id);
        foreach (var untouched in new[] { a, b })
        {
            Assert.Equal(before[untouched].Priority, after[untouched].Priority);
            Assert.Equal(before[untouched].UpdatedAt, after[untouched].UpdatedAt);
        }
        Assert.Equal(WiStatus.Backlog, after[c].Status);
        AssertAnnouncedOnly((c, RemoteWorkItemStatus.Backlog));
        _notifier.Verify(n => n.WorkItemBoardReorderedAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Dropping_above_the_top_card_makes_the_item_first()
    {
        var a = await SeedAsync(0, WiStatus.Ready);
        var b = await SeedAsync(1, WiStatus.Ready);
        var c = await SeedAsync(2, WiStatus.Ready);

        await _mgr.ReorderAsync(TestActors.Human, c, aboveId: null, belowId: a);

        Assert.Equal(new[] { c, a, b }, await BoardAsync());
        AssertAnnouncedOnly((c, RemoteWorkItemStatus.Ready));
    }

    [Fact]
    public async Task Dropping_below_the_last_card_makes_the_item_last()
    {
        var a = await SeedAsync(0);
        var b = await SeedAsync(1);
        var c = await SeedAsync(2);

        await _mgr.ReorderAsync(TestActors.Human, a, aboveId: c, belowId: null);

        Assert.Equal(new[] { b, c, a }, await BoardAsync());
        AssertAnnouncedOnly((a, RemoteWorkItemStatus.Backlog));
    }

    /// <summary>
    /// The card above the drop spot decides when there is one: the item lands
    /// directly after it in the whole board, ahead of a card of another column
    /// that sits between the two neighbours. Without one, the item lands
    /// directly before the card below.
    /// </summary>
    [Fact]
    public async Task An_item_of_another_column_between_the_neighbours_stays_out_of_the_way()
    {
        var a = await SeedAsync(0);
        var x = await SeedAsync(1, WiStatus.Ready);
        var b = await SeedAsync(2);
        var c = await SeedAsync(3);

        await _mgr.ReorderAsync(TestActors.Human, c, aboveId: a, belowId: b);
        Assert.Equal(new[] { a, c, x, b }, await BoardAsync());

        await _mgr.ReorderAsync(TestActors.Human, c, aboveId: null, belowId: b);
        Assert.Equal(new[] { a, x, c, b }, await BoardAsync());
    }

    /// <summary>
    /// With no number strictly between the two bounds — tied, or adjacent
    /// doubles — the board is renumbered: the item lands at the drop spot, every
    /// other item keeps its relative order and its UpdatedAt, and the moved item
    /// is announced once, the renumber once for the whole board.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_no_room_between_the_bounds_the_board_is_renumbered_in_the_same_order(bool adjacentNotTied)
    {
        var p = await SeedAsync(0);
        var q = await SeedAsync(1);
        var r = await SeedAsync(adjacentNotTied ? Math.BitIncrement(1d) : 1, WiStatus.Ready);
        var s = await SeedAsync(3, WiStatus.Done);
        var m = await SeedAsync(10);
        Assert.Equal(new[] { p, q, r, s, m }, await BoardAsync());
        var before = (await AllAsync()).ToDictionary(w => w.Id);

        await _mgr.ReorderAsync(TestActors.Human, m, aboveId: q, belowId: r);

        Assert.Equal(new[] { p, q, m, r, s }, await BoardAsync());
        var after = (await AllAsync()).ToDictionary(w => w.Id);
        Assert.Equal(after.Count, after.Values.Select(w => w.Priority).Distinct().Count());
        Assert.NotEqual(before[m].Priority, after[m].Priority);
        foreach (var other in after.Values.Where(w => w.Id != m))
            Assert.Equal(before[other.Id].UpdatedAt, other.UpdatedAt);
        foreach (var w in after.Values)
            Assert.Equal(before[w.Id].Status, w.Status);
        AssertAnnouncedOnly((m, RemoteWorkItemStatus.Backlog));
        _notifier.Verify(n => n.WorkItemBoardReorderedAsync(m), Times.Once);
    }
    /// <summary>
    /// A card dropped in another column is moved and placed by one server write:
    /// it lands in the new column at the drop spot, announced once as the status
    /// change it is.
    /// </summary>
    [Fact]
    public async Task A_drop_in_another_column_moves_and_places_the_item_at_once()
    {
        var a = await SeedAsync(0, WiStatus.Ready);
        var b = await SeedAsync(1, WiStatus.Ready);
        var x = await SeedAsync(5);

        var outcome = await _mgr.ReorderAsync(TestActors.Human, x, aboveId: a, belowId: b, targetStatus: RemoteWorkItemStatus.Ready);

        Assert.Equal(ReorderOutcome.Moved, outcome);
        Assert.Equal(new[] { a, x, b }, await BoardAsync());
        Assert.Equal(WiStatus.Ready, (await AllAsync()).Single(w => w.Id == x).Status);
        _notifier.Verify(n => n.WorkItemStateChangedAsync(x, RemoteWorkItemStatus.Backlog, RemoteWorkItemStatus.Ready), Times.Once);
        _notifier.Verify(n => n.WorkItemStateChangedAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()), Times.Once);
    }

    /// <summary>
    /// A drop that cannot be placed, or whose move the transition rules refuse,
    /// leaves the item in its column at its place, unannounced.
    /// </summary>
    [Fact]
    public async Task A_drop_in_another_column_that_cannot_land_leaves_the_item_where_it_was()
    {
        var a = await SeedAsync(0, WiStatus.Ready);
        var x = await SeedAsync(5);
        var before = (await AllAsync()).Single(w => w.Id == x);

        Assert.Equal(ReorderOutcome.InvalidNeighbour,
            await _mgr.ReorderAsync(TestActors.Human, x, aboveId: "999", belowId: null, targetStatus: RemoteWorkItemStatus.Done));
        Assert.Equal(ReorderOutcome.InvalidNeighbour,
            await _mgr.ReorderAsync(TestActors.Human, x, aboveId: null, belowId: null, targetStatus: RemoteWorkItemStatus.Ready));
        Assert.Equal(ReorderOutcome.TransitionRefused,
            await _mgr.ReorderAsync(TestActors.Human, x, aboveId: a, belowId: null, targetStatus: RemoteWorkItemStatus.Running));

        var after = (await AllAsync()).Single(w => w.Id == x);
        Assert.Equal((before.Status, before.Priority, before.UpdatedAt), (after.Status, after.Priority, after.UpdatedAt));
        _notifier.Verify(n => n.WorkItemStateChangedAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()), Times.Never);
    }
}
