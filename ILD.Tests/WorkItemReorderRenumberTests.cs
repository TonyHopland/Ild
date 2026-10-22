using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer.Dtos;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A renumber writes one item at a time, and the scheduler and every board read
/// the order between those writes: each step on the way has to leave the board
/// in order, and no card may move down while the renumber runs.
/// </summary>
public sealed class WorkItemReorderRenumberTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Mock<IWorkItemNotifier> _notifier = new();
    private readonly WorkItemManager _mgr;

    public WorkItemReorderRenumberTests()
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

    private async Task<string> SeedAsync(double priority)
    {
        var dto = await _db.Server.Service.CreateAsync(new CreateWorkItemRequest { Title = "w" }, Ct);
        await _db.Server.Service.UpdateAsync(dto.Id, new UpdateWorkItemRequest { Priority = priority }, Ct);
        _db.Server.ServerDb.ChangeTracker.Clear();
        return dto.Id;
    }

    private async Task<Dictionary<string, double>> PrioritiesAsync()
        => await _db.Server.ServerDb.WorkItems.AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Priority, Ct);

    private static List<string> InOrder(Dictionary<string, double> priorities)
        => priorities.OrderBy(p => p.Value).ThenBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key).ToList();

    [Fact]
    public async Task Every_step_of_a_renumber_keeps_the_board_in_order_and_moves_no_card_down()
    {
        var p = await SeedAsync(-5);
        var q = await SeedAsync(-4);
        var r = await SeedAsync(-4);
        var s = await SeedAsync(-3);
        var m = await SeedAsync(-2);
        var others = new[] { p, q, r, s };
        var previous = await PrioritiesAsync();
        var steps = 0;
        _notifier
            .Setup(n => n.WorkItemStateChangedAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()))
            .Returns(async () =>
            {
                steps++;
                var now = await PrioritiesAsync();
                Assert.Equal(others, InOrder(now).Where(id => id != m));
                Assert.All(now, kv => Assert.True(kv.Value <= previous[kv.Key], $"{kv.Key} moved down: {previous[kv.Key]} -> {kv.Value}"));
                previous = now;
            });

        await _mgr.ReorderAsync(TestActors.Human, m, aboveId: q, belowId: r);

        Assert.True(steps > 1, $"expected a renumber, saw {steps} write(s)");
        Assert.Equal(new[] { p, q, m, r, s }, InOrder(await PrioritiesAsync()));
    }
}
