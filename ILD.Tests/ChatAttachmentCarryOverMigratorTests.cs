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
        Assert.Equal(1, (await ChatAttachmentCarryOverMigrator.ApplyAsync(db.Context, captured)).Carried);

        // The second pass has no file left to read, so idempotency has to come
        // from the id already being present rather than from the read failing.
        var second = await ChatAttachmentCarryOverMigrator.ApplyAsync(db.Context, captured);
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
    public async Task Bytes_referenced_by_nothing_are_swept_off_disk_too()
    {
        using var db = new TestDb();
        var (session, _) = await SeedChatAsync(db);
        var orphan = WriteUpload(session, "orphan.png", "pixels");

        // No legacy column at all: nothing to carry, but the residue still goes.
        var result = await ChatAttachmentCarryOverMigrator.ApplyAsync(
            db.Context, await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context));

        Assert.Equal(0, result.Carried);
        Assert.Equal(1, result.Swept);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task Capture_is_empty_when_the_legacy_column_is_gone()
    {
        using var db = new TestDb();
        await SeedChatAsync(db);

        Assert.Empty(await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context));
    }

    private static async Task<ChatAttachmentCarryOverMigrator.CarryOverResult> CarryOverAsync(TestDb db)
        => await ChatAttachmentCarryOverMigrator.ApplyAsync(
            db.Context, await ChatAttachmentCarryOverMigrator.CaptureAsync(db.Context));

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
