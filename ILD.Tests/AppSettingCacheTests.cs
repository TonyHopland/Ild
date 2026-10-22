using System.Data.Common;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// AppSettings are read from the database once per process and kept until they are
/// saved through ILD. A setting edited in the database behind ILD's back is therefore
/// not seen until a restart, which is what these tests use to tell a cached read from
/// a database read.
/// </summary>
public class AppSettingCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The ILD data layer as the host registers it: one container, one scope per request.
    /// </summary>
    private static ServiceProvider Host(TestDb db)
    {
        var services = new ServiceCollection();
        var connection = db.Context.Database.GetDbConnection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection).WithPostgresSemantics());
        services.AddDataStores();
        return services.BuildServiceProvider();
    }

    private static async Task<string?> ReadInNewRequestAsync(ServiceProvider host, string key)
    {
        using var scope = host.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IAppSettingStore>().GetByKeyAsync(key, Ct))?.Value;
    }

    private static async Task SaveInNewRequestAsync(ServiceProvider host, string key, string value)
    {
        using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingStore>().UpsertAsync(key, value, Ct);
    }

    private static async Task EditBehindIldsBackAsync(TestDb db, string key, string value)
    {
        await using var context = db.Fresh();
        if (await context.AppSettings.AnyAsync(s => s.Key == key, Ct))
            await context.AppSettings.Where(s => s.Key == key).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value), Ct);
        else
        {
            context.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = key, Value = value });
            await context.SaveChangesAsync(Ct);
        }
    }

    private static async Task<string?> ValueInDatabaseAsync(TestDb db, string key)
    {
        await using var context = db.Fresh();
        return (await context.AppSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, Ct))?.Value;
    }

    private static AppDbContext ContextWith(TestDb db, IInterceptor interceptor)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(db.Context.Database.GetDbConnection())
            .WithPostgresSemantics()
            .AddInterceptors(interceptor)
            .Options);

    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    private static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

    [Fact]
    public async Task A_setting_is_read_from_the_database_once_per_process_and_again_after_a_save()
    {
        using var db = new TestDb();
        await db.Settings.UpsertAsync("scheduler.isPaused", "false", Ct);
        await using var host = Host(db);

        Assert.Equal("false", await ReadInNewRequestAsync(host, "scheduler.isPaused"));

        await EditBehindIldsBackAsync(db, "scheduler.isPaused", "edited-in-the-database");
        Assert.Equal("false", await ReadInNewRequestAsync(host, "scheduler.isPaused"));

        await SaveInNewRequestAsync(host, "scheduler.isPaused", "true");
        Assert.Equal("true", await ReadInNewRequestAsync(host, "scheduler.isPaused"));
    }

    [Fact]
    public async Task A_missing_setting_stays_missing_until_it_is_saved()
    {
        using var db = new TestDb();
        await using var host = Host(db);

        Assert.Null(await ReadInNewRequestAsync(host, AppSettingKeys.SessionIdleDays));

        await EditBehindIldsBackAsync(db, AppSettingKeys.SessionIdleDays, "7");
        Assert.Null(await ReadInNewRequestAsync(host, AppSettingKeys.SessionIdleDays));

        await SaveInNewRequestAsync(host, AppSettingKeys.SessionIdleDays, "14");
        Assert.Equal("14", await ReadInNewRequestAsync(host, AppSettingKeys.SessionIdleDays));
    }

    [Fact]
    public async Task A_read_that_loaded_the_old_value_while_a_save_committed_does_not_cache_it()
    {
        using var db = new TestDb();
        await db.Settings.UpsertAsync("workItemServer.url", "https://old.example.com", Ct);
        var cache = new AppSettingCache();
        var gate = new PauseAfterFirstRead();

        // The read has the old row in hand when the save commits, and only then finishes.
        await using var readerContext = ContextWith(db, gate);
        var inFlight = new AppSettingStore(readerContext, cache).GetByKeyAsync("workItemServer.url", Ct);
        await Within(gate.Loaded.Task);

        await using (var writerContext = db.Fresh())
            await Within(new AppSettingStore(writerContext, cache).UpsertAsync("workItemServer.url", "https://new.example.com", Ct));

        gate.Release.SetResult();
        await Within(inFlight);

        await using var nextContext = db.Fresh();
        Assert.Equal("https://new.example.com",
            (await new AppSettingStore(nextContext, cache).GetByKeyAsync("workItemServer.url", Ct))?.Value);
    }

    [Fact]
    public async Task After_two_concurrent_saves_of_one_setting_the_next_read_returns_what_the_database_holds()
    {
        using var db = new TestDb();
        await db.Settings.UpsertAsync("workItemServer.url", "https://old.example.com", Ct);
        var cache = new AppSettingCache();
        await using (var primeContext = db.Fresh())
            Assert.Equal("https://old.example.com",
                (await new AppSettingStore(primeContext, cache).GetByKeyAsync("workItemServer.url", Ct))?.Value);

        // The first save commits, then the second commits and finishes, and only
        // then does the first save finish.
        var gate = new PauseAfterSave();
        await using var firstContext = ContextWith(db, gate);
        var first = new AppSettingStore(firstContext, cache).UpsertAsync("workItemServer.url", "https://first.example.com", Ct);
        await Within(gate.Saved.Task);

        await using (var secondContext = db.Fresh())
            await Within(new AppSettingStore(secondContext, cache).UpsertAsync("workItemServer.url", "https://second.example.com", Ct));

        gate.Release.SetResult();
        await Within(first);

        await using var nextContext = db.Fresh();
        var read = (await new AppSettingStore(nextContext, cache).GetByKeyAsync("workItemServer.url", Ct))?.Value;
        Assert.Equal("https://second.example.com", await ValueInDatabaseAsync(db, "workItemServer.url"));
        Assert.Equal("https://second.example.com", read);
    }

    [Fact]
    public async Task The_work_item_server_options_come_from_the_cache_and_follow_a_save()
    {
        using var db = new TestDb();
        await using var host = Host(db);

        async Task<WorkItemServerOptions> ResolveInNewRequest()
        {
            using var scope = host.CreateScope();
            return await new DbWorkItemServerOptionsResolver(scope.ServiceProvider.GetRequiredService<IAppSettingStore>()).ResolveForWorkItemAsync("wi-1", Ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(ResolveInNewRequest);

        // What the WorkItem Server settings page saves.
        await SaveInNewRequestAsync(host, AppSettingKeys.WorkItemServerUrl, "https://workitems.example.com");
        await SaveInNewRequestAsync(host, AppSettingKeys.WorkItemServerApiKey, "key-one");
        var saved = await ResolveInNewRequest();
        Assert.Equal("https://workitems.example.com", saved.BaseUrl);
        Assert.Equal("key-one", saved.ApiKey);

        await EditBehindIldsBackAsync(db, AppSettingKeys.WorkItemServerUrl, "https://edited.example.com");
        await EditBehindIldsBackAsync(db, AppSettingKeys.WorkItemServerApiKey, "edited-key");
        using (var scope = host.CreateScope())
        {
            var resolver = new DbWorkItemServerOptionsResolver(scope.ServiceProvider.GetRequiredService<IAppSettingStore>());
            foreach (var options in new[]
                     {
                         await resolver.ResolveForWorkItemAsync("wi-1", Ct),
                         await resolver.ResolveForRepositoryAsync(Guid.NewGuid(), Ct),
                     })
            {
                Assert.Equal("https://workitems.example.com", options.BaseUrl);
                Assert.Equal("key-one", options.ApiKey);
            }
        }

        await SaveInNewRequestAsync(host, AppSettingKeys.WorkItemServerApiKey, "key-two");
        var rotated = await ResolveInNewRequest();
        Assert.Equal("key-two", rotated.ApiKey);

        await SaveInNewRequestAsync(host, AppSettingKeys.WorkItemServerUrl, string.Empty);
        await Assert.ThrowsAsync<InvalidOperationException>(ResolveInNewRequest);
    }

    /// <summary>
    /// Holds the first query's reader open after its row was read, until released.
    /// </summary>
    private sealed class PauseAfterFirstRead : DbCommandInterceptor
    {
        private int _paused;
        public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Loaded.SetResult();
                await Release.Task;
            }
            return result;
        }
    }

    /// <summary>
    /// Holds a save after it committed, before control returns to the store, until released.
    /// </summary>
    private sealed class PauseAfterSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saved.TrySetResult();
            await Release.Task;
            return result;
        }
    }
}
