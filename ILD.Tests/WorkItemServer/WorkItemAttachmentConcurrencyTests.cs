using ILD.WorkItemServer;
using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// Two uploads arriving at one work item at the same time. The per-item total is
/// a read-then-write, so without the owning row being claimed first both would
/// see room for themselves and the stored total would end up over the limit —
/// with no request to blame for it, since each was within the limit on its own.
///
/// A file-backed database on two connections, because that is the only way two
/// transactions can actually race; the shared in-memory database the other tests
/// use is a single connection.
/// </summary>
public sealed class WorkItemAttachmentConcurrencyTests : IAsyncLifetime
{
    private const long Megabyte = 1024 * 1024;
    private static readonly AttachmentLimits Limits = new()
    {
        MaxBytesPerFile = Megabyte,
        MaxTotalBytesPerWorkItem = Megabyte,
    };

    private string _directory = null!;
    private string _connectionString = null!;
    private WorkItemServerDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        _directory = Directory.CreateTempSubdirectory("ild-attachment-concurrency-").FullName;
        _connectionString = $"Data Source={Path.Combine(_directory, "workitems.db")}";
        _db = NewContext();
        await _db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private WorkItemServerDbContext NewContext()
        => new(new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_connectionString).Options);

    private static IncomingAttachment File(string name, int size)
    {
        var content = AttachmentUpload.Bytes(size);
        return new IncomingAttachment(name, "application/octet-stream", content.LongLength, _ => Task.FromResult(content));
    }

    [Fact]
    public async Task Two_uploads_racing_for_the_last_of_a_work_items_total_cannot_both_win()
    {
        var item = await new WorkItemService(_db, TimeProvider.System)
            .CreateAsync(new CreateWorkItemRequest { Title = "raced for" }, TestContext.Current.CancellationToken);

        // Either alone fits the 1 MB total; together they do not.
        await using var firstContext = NewContext();
        await using var secondContext = NewContext();
        var first = new WorkItemAttachmentService(firstContext, Limits, TimeProvider.System);
        var second = new WorkItemAttachmentService(secondContext, Limits, TimeProvider.System);

        var results = await Task.WhenAll(
            Task.Run(() => first.AddAsync(item.Id, new[] { File("first.bin", 600 * 1024) })),
            Task.Run(() => second.AddAsync(item.Id, new[] { File("second.bin", 600 * 1024) })));

        Assert.Equal(1, results.Count(r => r.Outcome == AddAttachmentsOutcome.Created));
        Assert.Equal(1, results.Count(r => r.Outcome == AddAttachmentsOutcome.TotalExceeded));

        await using var reader = NewContext();
        var stored = await reader.Set<WorkItemAttachment>().ToListAsync(TestContext.Current.CancellationToken);
        Assert.True(
            stored.Sum(a => a.SizeBytes) <= Limits.MaxTotalBytesPerWorkItem,
            $"the work item holds {stored.Sum(a => a.SizeBytes)} bytes, over its {Limits.MaxTotalBytesPerWorkItem} limit");
        Assert.Single(stored);
    }
}
