using System.Data.Common;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The WorkItem server's reorder: placement, a renumber and a move to another
/// column all happen in one transaction, so a drop either lands completely or
/// changes nothing.
/// </summary>
public sealed class WorkItemReorderServiceTests : IAsyncLifetime
{
    private readonly Recorder _recorder = new();
    private SqliteConnection _conn = null!;
    private DbContextOptions<WorkItemServerDbContext> _options = null!;
    private WorkItemServerDbContext _db = null!;
    private WorkItemService _svc = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _options = new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_conn).AddInterceptors(_recorder).Options;
        _db = new WorkItemServerDbContext(_options) { HistoryActor = TestActors.ServerHuman };
        await _db.Database.EnsureCreatedAsync(Ct);
        _svc = new WorkItemService(_db, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    /// <summary>Counts transactions and the statements that write an order value; can refuse a commit.</summary>
    private sealed class Recorder : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public List<string> PriorityWrites { get; } = new();
        public int Started { get; set; }
        public int Committed { get; set; }
        public bool FailCommit { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SET \"Priority\"", StringComparison.Ordinal))
                PriorityWrites.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            Started++;
            return ValueTask.FromResult(result);
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (FailCommit) throw new InvalidOperationException("commit refused by the test");
            return ValueTask.FromResult(result);
        }

        public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Committed++;
            return Task.CompletedTask;
        }

        public void Reset()
        {
            PriorityWrites.Clear();
            Started = 0;
            Committed = 0;
        }
    }

    private async Task<string> SeedAsync(double priority, WorkItemStatus status = WorkItemStatus.Backlog, string[]? deps = null)
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "w", ForceStatus = status, Dependencies = deps }, Ct);
        await _svc.UpdateAsync(dto.Id, new UpdateWorkItemRequest { Priority = priority }, Ct);
        return dto.Id;
    }

    private async Task<Dictionary<string, (double Priority, WorkItemStatus Status)>> StateAsync()
    {
        await using var read = new WorkItemServerDbContext(new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_conn).Options);
        return await read.WorkItems.AsNoTracking().ToDictionaryAsync(w => w.Id, w => (w.Priority, w.Status), Ct);
    }

    private async Task<IReadOnlyList<string>> BoardAsync()
        => (await StateAsync()).OrderBy(kv => kv.Value.Priority).ThenBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key).ToList();

    [Fact]
    public async Task A_renumber_is_one_statement_in_the_reorders_one_transaction()
    {
        var p = await SeedAsync(0);
        var q = await SeedAsync(1);
        var r = await SeedAsync(1, WorkItemStatus.Ready);
        var s = await SeedAsync(3, WorkItemStatus.Done);
        var m = await SeedAsync(10);
        _recorder.Reset();

        var resp = await _svc.ReorderAsync(m, new ReorderWorkItemRequest { AboveId = q, BelowId = r }, Ct);

        Assert.Equal(ReorderWorkItemOutcome.Moved, resp.Outcome);
        Assert.True(resp.Renumbered);
        Assert.Single(_recorder.PriorityWrites);
        Assert.Equal((1, 1), (_recorder.Started, _recorder.Committed));
        Assert.Equal(new[] { p, q, m, r, s }, await BoardAsync());
        var state = await StateAsync();
        Assert.Equal(state.Count, state.Values.Select(v => v.Priority).Distinct().Count());
        Assert.Equal(state[m].Priority, resp.Item!.Priority);
    }

    [Fact]
    public async Task A_renumber_whose_transaction_fails_leaves_every_item_as_it_was()
    {
        await SeedAsync(0);
        var q = await SeedAsync(1);
        var r = await SeedAsync(1);
        var m = await SeedAsync(10, WorkItemStatus.Ready);
        var before = await StateAsync();
        _db.ChangeTracker.Clear();
        _recorder.FailCommit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.ReorderAsync(m, new ReorderWorkItemRequest { AboveId = q, BelowId = r }, Ct));

        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task A_drop_in_another_column_moves_and_places_the_item_in_one_transaction()
    {
        var a = await SeedAsync(0, WorkItemStatus.Ready);
        var b = await SeedAsync(1, WorkItemStatus.Ready);
        var x = await SeedAsync(5);
        _recorder.Reset();

        var resp = await _svc.ReorderAsync(x, new ReorderWorkItemRequest
        {
            AboveId = a,
            BelowId = b,
            TargetStatus = WorkItemStatus.Ready,
            Reason = "dropped on the board",
        }, Ct);

        Assert.Equal(ReorderWorkItemOutcome.Moved, resp.Outcome);
        Assert.False(resp.Renumbered);
        Assert.Equal(WorkItemStatus.Ready, resp.Item!.Status);
        Assert.Equal((1, 1), (_recorder.Started, _recorder.Committed));
        Assert.Equal(new[] { a, x, b }, await BoardAsync());
        var status = Assert.Single((await _svc.GetHistoryAsync(x, Ct))!, e => e.Field == WorkItemHistoryFields.Status && e.NewValue == "Ready");
        Assert.Equal("dropped on the board", status.Reason);
    }

    [Fact]
    public async Task A_drop_the_server_cannot_place_or_move_leaves_the_item_where_it_was()
    {
        var unfinished = await SeedAsync(0);
        var a = await SeedAsync(1, WorkItemStatus.Running);
        var x = await SeedAsync(5, WorkItemStatus.Ready, new[] { unfinished });
        var before = await StateAsync();
        var history = (await _svc.GetHistoryAsync(x, Ct))!.Count;

        var refused = await _svc.ReorderAsync(x, new ReorderWorkItemRequest { AboveId = a, TargetStatus = WorkItemStatus.Running }, Ct);
        _db.ChangeTracker.Clear();
        var nowhere = await _svc.ReorderAsync(x, new ReorderWorkItemRequest { AboveId = "999", TargetStatus = WorkItemStatus.Backlog }, Ct);
        _db.ChangeTracker.Clear();
        var itself = await _svc.ReorderAsync(x, new ReorderWorkItemRequest { BelowId = x, TargetStatus = WorkItemStatus.Backlog }, Ct);
        var missing = await _svc.ReorderAsync("999", new ReorderWorkItemRequest { AboveId = a }, Ct);

        Assert.Equal(ReorderWorkItemOutcome.TransitionRefused, refused.Outcome);
        Assert.Equal("Dependencies not satisfied", refused.Reason);
        Assert.Equal(ReorderWorkItemOutcome.InvalidNeighbour, nowhere.Outcome);
        Assert.Equal(ReorderWorkItemOutcome.InvalidNeighbour, itself.Outcome);
        Assert.Equal(ReorderWorkItemOutcome.NotFound, missing.Outcome);
        Assert.Equal(before, await StateAsync());
        Assert.Equal(history, (await _svc.GetHistoryAsync(x, Ct))!.Count);
    }
}
