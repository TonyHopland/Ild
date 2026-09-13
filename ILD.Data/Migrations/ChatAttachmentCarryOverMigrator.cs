using System.Data;
using System.Text.Json;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Migrations;

/// <summary>
/// One-time, idempotent data migration that moves chat attachments off disk and
/// into <see cref="ChatAttachment"/> rows.
///
/// Attachments used to be files kept under the chat session's scratch directory
/// for the life of the chat, with only their metadata in the
/// <c>ChatMessages.AttachmentsJson</c> column. The bytes live in the database
/// now, because one agent uid serves every chat: anything durable in a scratch
/// directory is readable by every later agent. Dropping the column without this
/// would strand those files — referenced by nothing, still readable by that uid,
/// which is the property the move exists to remove.
///
/// It comes in two halves for the same reason as
/// <see cref="UserSessionCarryOverMigrator"/>: the schema migration destroys the
/// source (dropping the column) and creates the destination (the table), leaving
/// no moment at which a single pass could see both. <see cref="CaptureAsync"/>
/// runs <em>before</em> <c>Database.Migrate()</c> and reads the legacy column
/// with raw SQL; <see cref="ApplyAsync"/> runs <em>after</em> and writes the rows.
///
/// Both halves are no-ops on a database that never had the column, so this can be
/// left wired up until every deployment has started once on this version.
/// </summary>
public static class ChatAttachmentCarryOverMigrator
{
    /// <summary>The attachment metadata of one message, as the legacy column held it.</summary>
    public sealed record CarriedMessage(Guid MessageId, Guid SessionId, string Json);

    /// <param name="Carried">Attachments whose bytes are now database rows.</param>
    /// <param name="Missing">
    /// References whose file was already gone. Unrecoverable, so they are dropped
    /// rather than stored as empty rows — a transcript that offers a download
    /// which cannot work is worse than one that shows nothing.
    /// </param>
    /// <param name="Swept">Stale upload files deleted from the retired on-disk store.</param>
    public sealed record CarryOverResult(int Carried, int Missing, int Swept);

    /// <summary>Written with the serializer's defaults, so the names are PascalCase.</summary>
    private static readonly JsonSerializerOptions LegacyJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record LegacyAttachment(
        string? Id, string? FileName, string? StoredPath, string? ContentType, long SizeBytes);

    /// <summary>
    /// Reads the legacy metadata column. Returns empty when the column, the table
    /// or the database is not there — every one of which is the normal case on all
    /// but the single boot that performs this migration.
    /// </summary>
    public static async Task<IReadOnlyList<CarriedMessage>> CaptureAsync(
        AppDbContext db,
        CancellationToken ct = default)
    {
        var carried = new List<CarriedMessage>();
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await db.Database.OpenConnectionAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "Id", "ChatSessionId", "AttachmentsJson" FROM "ChatMessages"
                WHERE "AttachmentsJson" IS NOT NULL AND "AttachmentsJson" <> ''
                """;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                carried.Add(new CarriedMessage(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
        }
        catch
        {
            // No legacy column to read: nothing to carry across.
            return Array.Empty<CarriedMessage>();
        }

        return carried;
    }

    /// <summary>
    /// Writes each captured attachment's bytes into a row, deletes the file it
    /// came from, then clears whatever is left in the retired upload directories.
    /// </summary>
    public static async Task<CarryOverResult> ApplyAsync(
        AppDbContext db,
        IReadOnlyList<CarriedMessage> carried,
        CancellationToken ct = default)
    {
        var carriedCount = 0;
        var missing = 0;

        if (carried.Count > 0)
        {
            var alreadyCarried = (await db.ChatAttachments.Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            var now = DateTime.UtcNow;

            foreach (var message in carried)
            {
                foreach (var legacy in Parse(message.Json))
                {
                    if (string.IsNullOrEmpty(legacy.StoredPath) || string.IsNullOrEmpty(legacy.FileName))
                        continue;

                    // Reusing the id the metadata already carried keeps every
                    // download link that was handed out still resolving, and is
                    // what makes a second run of this a no-op.
                    var id = Guid.TryParse(legacy.Id, out var parsed) ? parsed : Guid.NewGuid();
                    if (!alreadyCarried.Add(id)) continue;

                    var content = await ReadBytesAsync(legacy.StoredPath, ct);
                    if (content is null)
                    {
                        missing++;
                        continue;
                    }

                    db.ChatAttachments.Add(new ChatAttachment
                    {
                        Id = id,
                        ChatSessionId = message.SessionId,
                        ChatMessageId = message.MessageId,
                        FileName = legacy.FileName,
                        ContentType = legacy.ContentType,
                        Content = content,
                        // The bytes that survived, not the size the metadata
                        // claimed — a truncated file should report what it is.
                        SizeBytes = content.LongLength,
                        CreatedAt = now,
                    });
                    carriedCount++;
                }
            }

            if (carriedCount > 0)
                await db.SaveChangesAsync(ct);
        }

        return new CarryOverResult(carriedCount, missing, await SweepUploadsAsync(db, ct));
    }

    private static IReadOnlyList<LegacyAttachment> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<LegacyAttachment>>(json, LegacyJson) ?? [];
        }
        catch (JsonException)
        {
            return Array.Empty<LegacyAttachment>();
        }
    }

    /// <summary>
    /// The bytes, or null when the file is gone or is a symlink. The scratch tree
    /// is agent-writable, so a link there could point anywhere; refusing to follow
    /// one keeps this from copying a file the agent chose into the database.
    /// </summary>
    private static async Task<byte[]?> ReadBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null) return null;
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Empties every session's upload directory. Safe to do wholesale: this runs
    /// at startup, before any turn is in flight, and an attachment is only written
    /// there for the turn that carries it — so anything present is residue of the
    /// retired store or of a turn that died before its cleanup ran.
    /// </summary>
    private static async Task<int> SweepUploadsAsync(AppDbContext db, CancellationToken ct)
    {
        var scratchPaths = await db.ChatSessions
            .Where(s => s.ScratchPath != null && s.ScratchPath != "")
            .Select(s => s.ScratchPath!)
            .ToListAsync(ct);

        var swept = 0;
        foreach (var scratchPath in scratchPaths)
        {
            var uploads = Path.Combine(scratchPath, "uploads");
            try
            {
                var directory = new DirectoryInfo(uploads);
                if (!directory.Exists || directory.LinkTarget is not null) continue;

                foreach (var file in directory.EnumerateFiles())
                {
                    file.Delete();
                    swept++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return swept;
    }
}
