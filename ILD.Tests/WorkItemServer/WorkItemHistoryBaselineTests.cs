using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The migration that adds the history table seeds one Baseline save per
/// existing item, so every item's history starts with its values at the time
/// history began. Production runs the seed on Postgres; it is written to run on
/// both, and here it runs on SQLite against items covering every enum value and
/// each way an item can have been created.
/// </summary>
public sealed class WorkItemHistoryBaselineTests : IAsyncLifetime
{
    private static readonly string[] TrackedFields =
    {
        "title", "description", "status", "priority", "tags", "dependencies", "repository",
        "aiProviderOverride", "aiProviderOverrideId", "branchNameOverride", "baseBranchOverride",
    };

    private SqliteConnection _conn = null!;
    private DbContextOptions<WorkItemServerDbContext> _options = null!;
    private int _nextId = 100;

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

    /// <summary>The migration that adds the history table, found by the table it creates.</summary>
    private static IReadOnlyList<MigrationOperation> HistoryMigrationUp()
    {
        var migrations = typeof(WorkItemServerDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Migration)Activator.CreateInstance(t)!)
            .Where(m => m.UpOperations.OfType<CreateTableOperation>().Any(c => c.Name == "WorkItemHistoryEntries"))
            .ToList();
        return Assert.Single(migrations).UpOperations;
    }

    /// <summary>The seed exactly as that migration runs it, frozen in the migration itself.</summary>
    private static string SeedSql() => Assert.Single(HistoryMigrationUp().OfType<SqlOperation>()).Sql;

    private async Task<WorkItem> InsertItemAsync(Action<WorkItem> shape)
    {
        var id = (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var item = new WorkItem
        {
            Id = id,
            Title = $"Item {id}",
            CreatedAt = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddMinutes(_nextId),
            UpdatedAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
        };
        shape(item);
        await using var db = new WorkItemServerDbContext(_options)
        {
            HistoryActor = new WorkItemActor("fixture", WorkItemActorKind.Human, null, null),
        };
        db.WorkItems.Add(item);
        await db.SaveChangesAsync(Ct);
        return item;
    }

    private async Task<List<WorkItemHistoryEntry>> SeedAndReadAsync()
    {
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = SeedSql();
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await using var db = new WorkItemServerDbContext(_options);
        return await db.WorkItemHistoryEntries.AsNoTracking()
            .Where(r => r.Kind == WorkItemHistoryKind.Baseline)
            .ToListAsync(Ct);
    }

    private static string? ValueOf(IEnumerable<WorkItemHistoryEntry> rows, string field)
        => Assert.Single(rows, r => r.Field == field).Value;

    private static void AssertSameGuid(Guid? expected, string? actual)
    {
        if (expected is null) Assert.Null(actual);
        else Assert.Equal(expected.Value.ToString("D"), actual, ignoreCase: true);
    }

    [Fact]
    public async Task The_seed_writes_one_baseline_save_per_item_holding_its_current_values()
    {
        var repository = Guid.NewGuid();
        var provider = Guid.NewGuid();
        var full = await InsertItemAsync(w =>
        {
            w.Description = "Line one\nLine two";
            w.TagsJson = "[\"feature\",\"ui\"]";
            w.DependenciesJson = "[\"7\",\"9\"]";
            w.RepositoryId = repository;
            w.AiProviderOverride = AiProviderOverrideMode.OverrideDefault;
            w.AiProviderOverrideId = provider;
            w.BranchNameOverride = "feature/login";
            w.BaseBranchOverride = "develop";
            w.CreatedBy = "alice";
        });
        var bare = await InsertItemAsync(_ => { });

        var rows = await SeedAndReadAsync();

        foreach (var item in new[] { full, bare })
        {
            var mine = rows.Where(r => r.WorkItemId == item.Id).ToList();
            Assert.Equal(TrackedFields.Order(StringComparer.Ordinal), mine.Select(r => r.Field).Order(StringComparer.Ordinal));
            Assert.All(mine, r =>
            {
                Assert.Equal(WorkItemHistoryKind.Baseline, r.Kind);
                Assert.Equal($"baseline-{item.Id}", r.SaveId);
                Assert.Equal(item.CreatedAt, r.Timestamp);
                Assert.Null(r.ActorWorkItemId);
                Assert.Null(r.Reason);
            });
            Assert.Equal(item.Title, ValueOf(mine, "title"));
            Assert.Equal(item.Description, ValueOf(mine, "description"));
            Assert.Equal(item.TagsJson, ValueOf(mine, "tags"));
            Assert.Equal(item.DependenciesJson, ValueOf(mine, "dependencies"));
            AssertSameGuid(item.RepositoryId, ValueOf(mine, "repository"));
            AssertSameGuid(item.AiProviderOverrideId, ValueOf(mine, "aiProviderOverrideId"));
            Assert.Equal(item.BranchNameOverride, ValueOf(mine, "branchNameOverride"));
            Assert.Equal(item.BaseBranchOverride, ValueOf(mine, "baseBranchOverride"));
        }
        Assert.Equal("OverrideDefault", ValueOf(rows.Where(r => r.WorkItemId == full.Id), "aiProviderOverride"));
        Assert.Equal("None", ValueOf(rows.Where(r => r.WorkItemId == bare.Id), "aiProviderOverride"));
        Assert.Null(ValueOf(rows.Where(r => r.WorkItemId == bare.Id), "description"));
    }

    [Fact]
    public async Task The_seed_stores_every_enum_value_by_its_name()
    {
        var expected = new List<(string Id, string Field, string Name)>();
        foreach (var status in Enum.GetValues<WorkItemStatus>())
            expected.Add(((await InsertItemAsync(w => w.Status = status)).Id, "status", WorkItemHistoryFields.Encode(status)!));
        // The seed ran while priority was still Low..Critical, stored as 0..3.
        foreach (var (stored, name) in new[] { (0d, "Low"), (1d, "Medium"), (2d, "High"), (3d, "Critical") })
            expected.Add(((await InsertItemAsync(w => w.Priority = stored)).Id, "priority", name));
        foreach (var mode in Enum.GetValues<AiProviderOverrideMode>())
            expected.Add(((await InsertItemAsync(w => w.AiProviderOverride = mode)).Id, "aiProviderOverride", WorkItemHistoryFields.Encode(mode)!));

        var rows = await SeedAndReadAsync();

        Assert.All(expected, e =>
            Assert.Equal(e.Name, ValueOf(rows.Where(r => r.WorkItemId == e.Id), e.Field)));
    }

    [Fact]
    public async Task The_seed_attributes_each_baseline_to_how_its_item_was_created()
    {
        var run = Guid.NewGuid();
        var chat = Guid.NewGuid();
        var byRun = await InsertItemAsync(w => { w.CreatedByLoopRunId = run; w.CreatedBy = "instance-user"; });
        var byChat = await InsertItemAsync(w => w.CreatedByChatSessionId = chat);
        var byHuman = await InsertItemAsync(w => w.CreatedBy = "alice");
        var byNobody = await InsertItemAsync(_ => { });

        var rows = await SeedAndReadAsync();
        WorkItemHistoryEntry AnyRow(WorkItem item) => rows.First(r => r.WorkItemId == item.Id);

        Assert.All(rows.Where(r => r.WorkItemId == byRun.Id), r =>
        {
            Assert.Equal("instance-user", r.Username);
            Assert.Equal(WorkItemActorKind.Agent, r.ActorKind);
            Assert.Equal($"run {run:D}", r.ActorDetail, ignoreCase: true);
            Assert.Null(r.ActorWorkItemId);
        });
        Assert.Null(AnyRow(byChat).Username);
        Assert.Equal(WorkItemActorKind.Chat, AnyRow(byChat).ActorKind);
        Assert.Equal($"chat {chat:D}", AnyRow(byChat).ActorDetail, ignoreCase: true);
        Assert.Equal("alice", AnyRow(byHuman).Username);
        Assert.Null(AnyRow(byHuman).ActorKind);
        Assert.Null(AnyRow(byHuman).ActorDetail);
        Assert.Null(AnyRow(byNobody).Username);
        Assert.Null(AnyRow(byNobody).ActorKind);
        Assert.Null(AnyRow(byNobody).ActorDetail);
    }

    [Fact]
    public void The_migration_that_adds_the_table_runs_the_seed_and_ties_history_to_no_work_item_row()
    {
        var operations = HistoryMigrationUp();
        var create = operations.OfType<CreateTableOperation>().Single(c => c.Name == "WorkItemHistoryEntries");
        Assert.Empty(create.ForeignKeys);
        Assert.Contains(operations.OfType<CreateIndexOperation>(), i =>
            i.Table == "WorkItemHistoryEntries" && i.Columns.SequenceEqual(new[] { "WorkItemId", "Field", "Timestamp" }));
        var seed = Assert.Single(operations.OfType<SqlOperation>());
        Assert.StartsWith("INSERT INTO \"WorkItemHistoryEntries\"", seed.Sql.Trim());
        Assert.True(operations.ToList().IndexOf(seed) > operations.ToList().IndexOf(create),
            "the seed has to run after the table exists");
    }
}
