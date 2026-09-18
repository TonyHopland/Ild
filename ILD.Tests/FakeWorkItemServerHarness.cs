using ILD.Core.Services.Remote;

using ILD.WorkItemServer;
using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// Bundles a real <see cref="WorkItemService"/> backed by a private
/// in-memory SQLite database and a <see cref="FakeWorkItemServerClient"/>
/// that delegates to it. Tests that exercise the remote-backed
/// WorkItemManager hold one of these for the lifetime of the test.
/// </summary>
public sealed class FakeWorkItemServerHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    public WorkItemServerDbContext ServerDb { get; }
    public IWorkItemService Service { get; }
    public IWorkItemAttachmentService Attachments { get; }
    public IWorkItemServerClient Client { get; }
    public IWorkItemServerOptionsResolver Options { get; } = new StubWorkItemServerOptionsResolver();

    public FakeWorkItemServerHarness(TimeProvider? clock = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        var opts = new DbContextOptionsBuilder<WorkItemServerDbContext>()
            .UseSqlite(_connection)
            .Options;
        ServerDb = new WorkItemServerDbContext(opts);
        ServerDb.Database.EnsureCreated();
        Service = new WorkItemService(ServerDb, clock ?? TimeProvider.System);
        // The real attachment service, reading the same environment the server
        // would, so the limits it enforces are the ones the test set before it.
        Attachments = new WorkItemAttachmentService(
            ServerDb, AttachmentLimits.FromEnvironment(), clock ?? TimeProvider.System);
        Client = new FakeWorkItemServerClient(Service, Attachments);
    }

    public void Dispose()
    {
        ServerDb.Dispose();
        _connection.Dispose();
    }
}
