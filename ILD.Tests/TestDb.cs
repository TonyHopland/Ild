using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// Per-test SQLite-in-memory database with one shared <see cref="AppDbContext"/>
/// and one store instance per repository.
///
/// Isolation guarantees:
/// <list type="bullet">
///   <item>Each <c>TestDb</c> instance opens its own private <see cref="SqliteConnection"/>
///         (Filename=:memory:) so two tests never share data even when they run in parallel.</item>
///   <item>The connection lives only as long as this instance — schema is destroyed on <see cref="Dispose"/>.</item>
///   <item>The exposed stores all wrap the same tracked <c>Context</c>; tests that need to
///         observe writes done through one store from another should mutate via <c>Context</c>
///         (or call <see cref="Fresh"/> to get an untracked context on the same connection)
///         to avoid stale change-tracker reads.</item>
///   <item>End-to-end / integration tests must NOT share a <c>TestDb</c>; use the per-test
///         <see cref="ILD.Tests.Integration.ApiFactory"/> instead, which boots the full API
///         pipeline against its own in-memory connection and temp data directory.</item>
/// </list>
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContext Context { get; }
    public ILoopRunStore LoopRuns { get; }
    public ILoopTemplateStore LoopTemplates { get; }
    public IEventLogStore EventLogs { get; }
    public IAuthStore Auth { get; }
    public IProviderStore Providers { get; }

    /// <summary>
    /// Fake WorkItemServer harness backing the remote-backed
    /// <c>WorkItemManager</c>. Tests that construct a manager pass
    /// <see cref="ServerClient"/> + <see cref="ServerOptions"/> through.
    /// Owned by this instance unless one was supplied to the constructor — a
    /// caller-supplied harness outlives the <c>TestDb</c>, which is how a test
    /// stands a fresh ILD instance up against a server that has been running
    /// all along.
    /// </summary>
    public FakeWorkItemServerHarness Server { get; }

    private readonly bool _ownsServer;
    public IWorkItemServerClient ServerClient => Server.Client;
    public IWorkItemServerOptionsResolver ServerOptions => Server.Options;

    /// <param name="server">
    /// An existing WorkItemServer harness to attach to instead of standing up a
    /// fresh one. Lets a test replace the ILD-local database — an instance
    /// reset — while the work-item server keeps everything it was told.
    /// </param>
    public TestDb(FakeWorkItemServerHarness? server = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new AppDbContext(options);
        Context.Database.EnsureCreated();
        LoopRuns = new LoopRunStore(Context);
        LoopTemplates = new LoopTemplateStore(Context);
        EventLogs = new EventLogStore(Context);
        Auth = new AuthStore(Context);
        Providers = new ProviderStore(Context);
        _ownsServer = server is null;
        Server = server ?? new FakeWorkItemServerHarness();
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
        if (_ownsServer) Server.Dispose();
    }
}
