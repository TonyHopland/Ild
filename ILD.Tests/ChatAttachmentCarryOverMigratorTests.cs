using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using ILD.Data.Entities;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The carry-across that moves chat attachments off disk and into rows. The
/// current schema has no <c>ChatMessages.AttachmentsJson</c>, so each test
/// re-creates the legacy column to stand in for a database on the version that
/// kept attachments as files.
/// </summary>
public sealed class ChatAttachmentCarryOverMigratorTests : IDisposable
{
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(), "ild-attachment-carryover-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Stands in for the real redirected-path check, which lives in ILD.Core.</summary>
    private static readonly Func<string, bool> Trusted = _ => true;

    private static readonly Func<string, bool> Redirected = _ => false;

    public void Dispose()
    {
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_file_on_disk_becomes_a_row_and_stops_being_a_file()
    {
        using var db = new TestDb();
        var (session, message) = await SeedChatAsync(db);
        var stored = WriteUpload(session, "sketch.png", "pixels");
        await SeedLegacyColumnAsync(db, message, Reference(stored, "sketch.png", "image/png"));

        var result = await CarryOverAsync(db);

        Assert.Equal(1, result.Carried);
        var row = await db.Context.ChatAttachments.SingleAsync();
        Assert.Equal("sketch.png", row.FileName);
        Assert.Equal("image/png", row.ContentType);
        Assert.Equal("pixels", Encoding.UTF8.GetString(row.Content));
        Assert.Equal(6, row.SizeBytes);
        Assert.Equal(message.Id, row.ChatMessageId);
        Assert.Equal(session.Id, row.ChatSessionId);

        // The point of the exercise: the bytes no longer sit where the agent uid
        // could read them.
        Assert.False(File.Exists(stored));
    }

    [Fact]
    public async Task The_carried_row_keeps_the_id_its_download_link_already_used()
    {
        using var db = new TestDb();
        var (session, message) = await SeedChatAsync(db);
        var id = Guid.NewGuid();
        var stored = WriteUpload(session, "sketch.png", "pixels");
        await SeedLegacyColumnAsync(db, message, Reference(stored, "sketch.png", "image/png", id));

        await CarryOverAsync(db);

        Assert.Equal(id, (await db.Context.ChatAttachments.SingleAsync()).Id);
    }

    [Fact]
    public async Task Carrying_the_same_capture_twice_leaves_one_row()
    {
        using var db = new TestDb();
        var (session, message) = await SeedChatAsync(db);
        var stored = WriteUpload(session, "sketch.png", "pixels");
        await SeedLegacyColumnAsync(db, message, Reference(stored, "sketch.png", "image/png"));

        var captured = await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context);
        Assert.Equal(1, (await ChatAttachmentCarryOverMigrator.ApplyAsync(db.Context, captured, Trusted)).Carried);

        // The second pass has no file left to read, so idempotency has to come
        // from the id already being present rather than from the read failing.
        var second = await ChatAttachmentCarryOverMigrator.ApplyAsync(db.Context, captured, Trusted);
        Assert.Equal(0, second.Carried);
        Assert.Equal(0, second.Missing);
        Assert.Equal(1, await db.Context.ChatAttachments.CountAsync());
    }

    [Fact]
    public async Task A_reference_whose_file_is_already_gone_is_dropped_not_stored_empty()
    {
        using var db = new TestDb();
        var (session, message) = await SeedChatAsync(db);
        var vanished = Path.Combine(UploadsOf(session), "wiped-by-a-redeploy.png");
        Directory.CreateDirectory(UploadsOf(session));
        await SeedLegacyColumnAsync(db, message, Reference(vanished, "wiped-by-a-redeploy.png", "image/png"));

        var result = await CarryOverAsync(db);

        Assert.Equal(0, result.Carried);
        Assert.Equal(1, result.Missing);
        Assert.Empty(db.Context.ChatAttachments);
    }

    [Fact]
    public async Task A_symlinked_upload_is_not_followed_into_the_database()
    {
        using var db = new TestDb();
        var (session, message) = await SeedChatAsync(db);
        var secret = Path.Combine(_scratchRoot, "secret.txt");
        Directory.CreateDirectory(_scratchRoot);
        await File.WriteAllTextAsync(secret, "not the agent's to take");

        var uploads = UploadsOf(session);
        Directory.CreateDirectory(uploads);
        var link = Path.Combine(uploads, "sketch.png");
        File.CreateSymbolicLink(link, secret);
        await SeedLegacyColumnAsync(db, message, Reference(link, "sketch.png", "image/png"));

        var result = await CarryOverAsync(db);

        Assert.Equal(0, result.Carried);
        Assert.Empty(db.Context.ChatAttachments);
        // The link goes; what it pointed at is none of this migration's business.
        Assert.True(File.Exists(secret));
    }

    [Fact]
    public async Task A_redirected_path_is_neither_read_nor_swept()
    {
        using var db = new TestDb();
        var (session, message) = await SeedChatAsync(db);
        var stored = WriteUpload(session, "sketch.png", "pixels");
        await SeedLegacyColumnAsync(db, message, Reference(stored, "sketch.png", "image/png"));

        var captured = await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context);
        var result = await ChatAttachmentCarryOverMigrator.ApplyAsync(db.Context, captured, Redirected);

        // A redirected component means these deletes would land somewhere other
        // than where they were aimed, so none of them happen.
        Assert.Equal(0, result.Carried);
        Assert.Equal(0, result.Swept);
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public async Task Bytes_referenced_by_nothing_are_swept_off_disk_too()
    {
        using var db = new TestDb();
        var (session, _) = await SeedChatAsync(db);
        var orphan = WriteUpload(session, "orphan.png", "pixels");

        // No legacy column at all: nothing to carry, but the residue still goes.
        var result = await CarryOverAsync(db);

        Assert.Equal(0, result.Carried);
        Assert.Equal(1, result.Swept);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task Capture_is_empty_but_complete_when_the_legacy_column_is_gone()
    {
        using var db = new TestDb();
        await SeedChatAsync(db);

        var captured = await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context);

        Assert.Empty(captured.Messages);
        // Complete, because "there is no such column" is an answer rather than a
        // failure — this is the state every boot after the first one is in.
        Assert.True(captured.Complete);
    }

    /// <summary>
    /// The one that matters most. A read that fails is not a chat with no
    /// attachments: the files it would have described are still on disk, and
    /// sweeping them would destroy exactly what this migration exists to rescue.
    /// </summary>
    [Fact]
    public async Task A_capture_that_could_not_be_read_sweeps_nothing()
    {
        using var db = new TestDb();
        var (session, _) = await SeedChatAsync(db);
        var stranded = WriteUpload(session, "sketch.png", "pixels");

        var result = await ChatAttachmentCarryOverMigrator.ApplyAsync(
            db.Context, ChatAttachmentCarryOverMigrator.LegacyCapture.Unreadable, Trusted);

        Assert.Equal(0, result.Swept);
        Assert.True(File.Exists(stranded));
    }

    /// <summary>
    /// Reports itself open and answers nothing at all. Stands in for a database
    /// that went away between being opened and being read — the one window in
    /// which a failed probe is indistinguishable from a schema that simply has no
    /// such column, and a real connection cannot be coaxed into it, since an open
    /// one always answers <c>SELECT 1</c>.
    /// </summary>
    private sealed class DeadConnection : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new InvalidOperationException();
        public override void Close() { }
        public override void Open() => throw new InvalidOperationException("connection lost");

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new InvalidOperationException();

        protected override DbCommand CreateDbCommand() => throw new InvalidOperationException("connection lost");
    }

    [Fact]
    public async Task A_connection_that_died_after_opening_is_unreadable_not_empty()
    {
        using var db = new TestDb();
        var (session, _) = await SeedChatAsync(db);
        var stranded = WriteUpload(session, "sketch.png", "pixels");

        var captured = await ChatAttachmentCarryOverMigrator.CaptureFromAsync(new DeadConnection());

        // Emphatically not "there is nothing to carry": that answer is what
        // licences the sweep, and the files are still out there.
        Assert.False(captured.Complete);
        Assert.Empty(captured.Messages);

        var result = await ChatAttachmentCarryOverMigrator.ApplyAsync(db.Context, captured, Trusted);
        Assert.Equal(0, result.Swept);
        Assert.True(File.Exists(stranded));
    }

    [Fact]
    public async Task A_live_connection_without_the_table_is_nothing_to_carry()
    {
        using var db = new TestDb();
        await db.Context.Database.ExecuteSqlRawAsync(@"DROP TABLE ""ChatMessages""");

        var captured = await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context);

        // The liveness gate must not over-trigger either: a healthy database that
        // simply has no such table is an answer, and the sweep stays available.
        Assert.True(captured.Complete);
        Assert.Empty(captured.Messages);
    }

    private static async Task<ChatAttachmentCarryOverMigrator.CarryOverResult> CarryOverAsync(TestDb db)
        => await ChatAttachmentCarryOverMigrator.ApplyAsync(
            db.Context, await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context), Trusted);

    private string UploadsOf(ChatSession session) => Path.Combine(session.ScratchPath, "uploads");

    private string WriteUpload(ChatSession session, string name, string content)
    {
        var uploads = UploadsOf(session);
        Directory.CreateDirectory(uploads);
        var path = Path.Combine(uploads, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// One entry as the retired column held it — serializer defaults, so the
    /// property names are PascalCase.
    /// </summary>
    private static object Reference(string storedPath, string fileName, string? contentType, Guid? id = null)
        => new
        {
            Id = (id ?? Guid.NewGuid()).ToString(),
            FileName = fileName,
            StoredPath = storedPath,
            ContentType = contentType,
            SizeBytes = 6L,
        };

    private async Task<(ChatSession Session, ChatMessage Message)> SeedChatAsync(TestDb db)
    {
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = "alice",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "read",
            ScratchPath = Path.Combine(_scratchRoot, Guid.NewGuid().ToString("N")),
            CreatedAt = DateTime.UtcNow,
        };
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = session.Id,
            Role = "user",
            Content = "what is wrong here?",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.ChatSessions.Add(session);
        db.Context.ChatMessages.Add(message);
        await db.Context.SaveChangesAsync();
        return (session, message);
    }

    /// <summary>
    /// Re-creates the dropped <c>ChatMessages.AttachmentsJson</c> column and puts
    /// <paramref name="references"/> in it, exactly as the previous version would.
    /// </summary>
    private static async Task SeedLegacyColumnAsync(TestDb db, ChatMessage message, params object[] references)
    {
        await db.Context.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE ""ChatMessages"" ADD COLUMN ""AttachmentsJson"" TEXT");
        await db.Context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ChatMessages"" SET ""AttachmentsJson"" = {0} WHERE ""Id"" = {1}",
            JsonSerializer.Serialize(references),
            message.Id);
    }
}
