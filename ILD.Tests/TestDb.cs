using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext Context { get; }
    public IWorkItemStore WorkItems { get; }
    public ILoopRunStore LoopRuns { get; }
    public ILoopTemplateStore LoopTemplates { get; }
    public IEventLogStore EventLogs { get; }
    public IAuthStore Auth { get; }
    public IProviderStore Providers { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new AppDbContext(options);
        Context.Database.EnsureCreated();

        WorkItems = new WorkItemStore(Context);
        LoopRuns = new LoopRunStore(Context);
        LoopTemplates = new LoopTemplateStore(Context);
        EventLogs = new EventLogStore(Context);
        Auth = new AuthStore(Context);
        Providers = new ProviderStore(Context);
    }

    public AppDbContext Fresh()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options);
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
