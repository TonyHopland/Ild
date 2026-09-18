using ILD.WorkItemServer;
using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// Every limit is answered from the size the request declares, so nothing is
/// copied for an upload that will be refused. What is stored, though, is what
/// was actually read — so the service stores a file only after checking that the
/// two agree. A reader that hands over more than it said would otherwise put
/// bytes in the database that no limit ever measured.
/// </summary>
public sealed class WorkItemAttachmentStoredSizeTests : IAsyncLifetime
{
    private const long Kilobyte = 1024;

    private SqliteConnection _connection = null!;
    private WorkItemServerDbContext _db = null!;
    private string _workItemId = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _db = new WorkItemServerDbContext(
            new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _workItemId = (await new WorkItemService(_db, TimeProvider.System)
            .CreateAsync(new CreateWorkItemRequest { Title = "declared sizes" })).Id;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private WorkItemAttachmentService Service(long maxBytesPerFile) => new(
        _db,
        new AttachmentLimits { MaxBytesPerFile = maxBytesPerFile, MaxTotalBytesPerWorkItem = 64 * Kilobyte },
        TimeProvider.System);

    private static IncomingAttachment File(long declared, int actual)
        => new("claim.bin", "application/octet-stream", declared, _ => Task.FromResult(AttachmentUpload.Bytes(actual)));

    [Theory]
    [InlineData(16, 32)]
    [InlineData(16, 8)]
    public async Task A_reader_that_hands_over_a_different_size_than_it_declared_is_refused(long declared, int actual)
    {
        var result = await Service(8 * Kilobyte).AddAsync(_workItemId, new[] { File(declared, actual) });

        Assert.Equal(AddAttachmentsOutcome.SizeMismatch, result.Outcome);
        Assert.Contains(actual.ToString(), result.Error!, StringComparison.Ordinal);
        Assert.Empty(result.Created);
        Assert.Empty(await _db.Set<WorkItemAttachment>().ToListAsync());
    }

    [Fact]
    public async Task A_reader_that_hands_over_more_than_the_per_file_limit_is_refused_by_that_limit()
    {
        // Declared within the limit, read well past it: the answer names the
        // limit that was broken rather than the declaration that lied.
        var result = await Service(Kilobyte).AddAsync(_workItemId, new[] { File(declared: Kilobyte, actual: 4096) });

        Assert.Equal(AddAttachmentsOutcome.FileTooLarge, result.Outcome);
        Assert.Contains("per file", result.Error!, StringComparison.Ordinal);
        Assert.Empty(await _db.Set<WorkItemAttachment>().ToListAsync());
    }

    [Fact]
    public async Task A_reader_that_keeps_its_word_is_stored_with_the_size_it_was_read_at()
    {
        var result = await Service(8 * Kilobyte).AddAsync(_workItemId, new[] { File(declared: 512, actual: 512) });

        Assert.Equal(AddAttachmentsOutcome.Created, result.Outcome);
        Assert.Equal(512, Assert.Single(result.Created).SizeBytes);
        Assert.Equal(512, Assert.Single(await _db.Set<WorkItemAttachment>().ToListAsync()).Content.Length);
    }
}
