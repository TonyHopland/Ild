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
/// The poll every ILD instance runs every 30 seconds carries the whole active
/// and ready sets, so nothing new may ride along on it: attachment metadata
/// belongs on the reads a human or an agent asks for, and the heartbeat must not
/// grow a second query per item to carry it.
/// </summary>
public class WorkItemAttachmentPollTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private CommandRecorder _commands = null!;
    private WorkItemServerDbContext _db = null!;
    private WorkItemService _service = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _commands = new CommandRecorder();
        var options = new DbContextOptionsBuilder<WorkItemServerDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_commands)
            .Options;
        _db = new WorkItemServerDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        _service = new WorkItemService(_db, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<WorkItemDto> SeedItemWithAttachmentAsync(WorkItemStatus status)
    {
        var item = await _service.CreateAsync(new CreateWorkItemRequest { Title = "attached", ForceStatus = status });
        var owner = await _db.WorkItems.SingleAsync(w => w.Id == item.Id);
        _db.Set<WorkItemAttachment>().Add(new WorkItemAttachment
        {
            Id = Guid.NewGuid(),
            WorkItemId = owner.InternalId,
            FileName = "sketch.png",
            ContentType = "image/png",
            SizeBytes = 3,
            Content = new byte[] { 1, 2, 3 },
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task A_poll_carries_no_attachment_data_and_queries_no_attachment_row()
    {
        var active = await SeedItemWithAttachmentAsync(WorkItemStatus.Running);
        var ready = await SeedItemWithAttachmentAsync(WorkItemStatus.Ready);

        _commands.Clear();
        var poll = await _service.PollAsync(new[] { active.Id });

        Assert.Empty(Assert.Single(poll.ActiveItems, i => i.Id == active.Id).Attachments);
        Assert.Empty(Assert.Single(poll.ReadyItems, i => i.Id == ready.Id).Attachments);
        Assert.DoesNotContain(_commands.Executed, sql => sql.Contains("Attachment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_single_read_does_carry_the_attachment_metadata()
    {
        var item = await SeedItemWithAttachmentAsync(WorkItemStatus.Running);

        var fetched = await _service.GetAsync(item.Id);

        var attachment = Assert.Single(fetched!.Attachments);
        Assert.Equal("sketch.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(3, attachment.SizeBytes);
    }

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> _executed = new();

        public IReadOnlyList<string> Executed
        {
            get { lock (_executed) return _executed.ToList(); }
        }

        public void Clear()
        {
            lock (_executed) _executed.Clear();
        }

        private void Record(DbCommand command)
        {
            lock (_executed) _executed.Add(command.CommandText);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }
    }
}
