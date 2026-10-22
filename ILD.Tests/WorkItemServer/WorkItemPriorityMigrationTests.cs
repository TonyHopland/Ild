using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The migration that turns the priority into the board order value gives every
/// existing item a unique value reproducing the order the board showed before:
/// newest first, and the later-inserted item first when two were created at the
/// same instant. Production runs its data step on Postgres; it is written to run
/// on both, and here it runs on SQLite.
/// </summary>
public sealed class WorkItemPriorityMigrationTests : IAsyncLifetime
{
    private static readonly DateTime Epoch = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private SqliteConnection _conn = null!;
    private DbContextOptions<WorkItemServerDbContext> _options = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _options = new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_conn).Options;
        await using var db = new WorkItemServerDbContext(_options);
        await db.Database.EnsureCreatedAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();

    /// <summary>The migration that changes the priority column, found by that change.</summary>
    private static Migration PriorityMigration()
    {
        var migrations = typeof(WorkItemServerDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Migration)Activator.CreateInstance(t)!)
            .Where(m => m.UpOperations.OfType<AlterColumnOperation>()
                .Any(a => a.Table == "WorkItems" && a.Name == "Priority" && a.ClrType == typeof(double)))
            .ToList();
        return Assert.Single(migrations);
    }

    private async Task RunAsync(IEnumerable<SqlOperation> steps)
    {
        foreach (var step in steps)
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = step.Sql;
            await cmd.ExecuteNonQueryAsync(Ct);
        }
    }

    /// <summary>Inserts items in the given order, so later ones get the higher InternalId.</summary>
    private async Task<List<WorkItem>> InsertAsync(params (string Title, DateTime CreatedAt, double OldPriority)[] items)
    {
        await using var db = new WorkItemServerDbContext(_options)
        {
            HistoryActor = new WorkItemActor("fixture", WorkItemActorKind.Human, null, null),
        };
        var inserted = new List<WorkItem>();
        foreach (var (title, createdAt, oldPriority) in items)
        {
            var item = new WorkItem
            {
                Id = $"tmp-{title}",
                Title = title,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                Priority = oldPriority,
            };
            db.WorkItems.Add(item);
            await db.SaveChangesAsync(Ct);
            inserted.Add(item);
        }
        return inserted;
    }

    private async Task<Dictionary<string, double>> PrioritiesAsync()
    {
        await using var db = new WorkItemServerDbContext(_options);
        return await db.WorkItems.AsNoTracking().ToDictionaryAsync(w => w.Title, w => w.Priority, Ct);
    }

    [Fact]
    public async Task Up_numbers_every_item_in_the_old_board_order_newest_first()
    {
        await InsertAsync(
            ("oldest", Epoch, 3),
            ("newest", Epoch.AddDays(3), 0),
            ("tie-first-inserted", Epoch.AddDays(1), 1),
            ("tie-second-inserted", Epoch.AddDays(1), 1),
            ("middle", Epoch.AddDays(2), 2));

        await RunAsync(PriorityMigration().UpOperations.OfType<SqlOperation>());

        var priorities = await PrioritiesAsync();
        Assert.Equal(0d, priorities["newest"]);
        Assert.Equal(1d, priorities["middle"]);
        Assert.Equal(2d, priorities["tie-second-inserted"]);
        Assert.Equal(3d, priorities["tie-first-inserted"]);
        Assert.Equal(4d, priorities["oldest"]);
    }

    [Fact]
    public async Task Down_resets_every_item_to_medium()
    {
        await InsertAsync(("a", Epoch, -12.5), ("b", Epoch.AddDays(1), 0), ("c", Epoch.AddDays(2), 7.25));

        await RunAsync(PriorityMigration().DownOperations.OfType<SqlOperation>());

        Assert.All((await PrioritiesAsync()).Values, p => Assert.Equal(1d, p));
    }
}
