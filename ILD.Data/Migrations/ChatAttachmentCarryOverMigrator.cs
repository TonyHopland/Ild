using System.Data;
using System.Data.Common;
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

    /// <param name="Messages">What the legacy column held, empty when it held nothing.</param>
    /// <param name="Complete">
    /// Whether the legacy metadata was read through. False means the column was
    /// there but could not be read, which is emphatically not the same as it
    /// holding nothing: the files it pointed at are then unaccounted for, and
    /// <see cref="ApplyAsync"/> leaves every one of them alone rather than sweep
    /// away bytes it never managed to store.
    /// </param>
    public sealed record LegacyCapture(IReadOnlyList<CarriedMessage> Messages, bool Complete)
    {
        /// <summary>Nothing to carry, established rather than assumed.</summary>
        public static LegacyCapture Nothing { get; } = new(Array.Empty<CarriedMessage>(), Complete: true);

        /// <summary>The metadata exists but could not be read.</summary>
        public static LegacyCapture Unreadable { get; } = new(Array.Empty<CarriedMessage>(), Complete: false);
    }

    /// <param name="Carried">Attachments whose bytes are now database rows.</param>
    /// <param name="Missing">
    /// References whose file was already gone, or sat behind a redirected path.
    /// Unrecoverable, so they are dropped rather than stored as empty rows — a
    /// transcript that offers a download which cannot work is worse than one that
    /// shows nothing.
    /// </param>
    /// <param name="Swept">Stale upload files deleted from the retired on-disk store.</param>
    public sealed record CarryOverResult(int Carried, int Missing, int Swept);

    /// <summary>Written with the serializer's defaults, so the names are PascalCase.</summary>
    private static readonly JsonSerializerOptions LegacyJson = new() { PropertyNameCaseInsensitive = true };

    private sealed record LegacyAttachment(
        string? Id, string? FileName, string? StoredPath, string? ContentType, long SizeBytes);

    /// <summary>
    /// Reads the legacy metadata column.
    ///
    /// <para>
    /// The three outcomes are deliberately distinct. A database that cannot be
    /// opened is <see cref="LegacyCapture.Unreadable"/>. A database whose schema
    /// simply has no such column — a fresh install, or one already migrated — is
    /// <see cref="LegacyCapture.Nothing"/>, which is the normal case on every boot
    /// but one. A column that is there but throws while being read is
    /// <see cref="LegacyCapture.Unreadable"/> again, because treating a failed read
    /// as an empty one would let the caller delete the files it describes.
    /// </para>
    /// </summary>
    public static async Task<LegacyCapture> CaptureAsync(
        AppDbContext db,
        CancellationToken ct = default)
    {
        DbConnection connection;
        try
        {
            connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await db.Database.OpenConnectionAsync(ct);
        }
        catch
        {
            // Cannot even reach the database: nothing is known about what is on
            // disk, so nothing on disk may be touched.
            return LegacyCapture.Unreadable;
        }

        return await CaptureFromAsync(connection, ct);
    }

    /// <summary>
    /// The read itself, against a connection that is already open. Separate
    /// because the context is consulted for nothing else, and because the
    /// distinction drawn here — between a schema that lacks the column and a
    /// database that has stopped answering — is the one worth testing directly.
    /// </summary>
    public static async Task<LegacyCapture> CaptureFromAsync(
        DbConnection connection,
        CancellationToken ct = default)
    {
        // A failed probe means nothing on its own. The column may be absent, or
        // the connection may have died since it was opened, and only the first of
        // those makes it safe to conclude there is nothing to carry. The second
        // would hand back a clean bill of health and let the caller sweep the
        // files away, which is the loss this whole split exists to prevent — so a
        // failed probe is believed only from a connection that still answers.
        if (!await QuerySucceedsAsync(connection, @"SELECT 1 FROM ""ChatMessages"" WHERE 1 = 0", ct))
            return await NothingIfLiveAsync(connection, ct);
        if (!await QuerySucceedsAsync(connection, @"SELECT ""AttachmentsJson"" FROM ""ChatMessages"" WHERE 1 = 0", ct))
            return await NothingIfLiveAsync(connection, ct);

        var carried = new List<CarriedMessage>();
        try
        {
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
            // The column is there, so this is a real failure and not the ordinary
            // "already migrated" case. Whatever was collected before it threw is
            // an arbitrary prefix of the truth, so none of it is reported.
            return LegacyCapture.Unreadable;
        }

        return new LegacyCapture(carried, Complete: true);
    }

    private static async Task<bool> QuerySucceedsAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="LegacyCapture.Nothing"/>, but only on a connection that still
    /// answers a question of its own. <c>SELECT 1</c> names no table, so there is
    /// only one thing its failure can mean.
    /// </summary>
    private static async Task<LegacyCapture> NothingIfLiveAsync(DbConnection connection, CancellationToken ct)
        => await QuerySucceedsAsync(connection, "SELECT 1", ct)
            ? LegacyCapture.Nothing
            : LegacyCapture.Unreadable;

    /// <summary>
    /// Writes each captured attachment's bytes into a row, then clears what the
    /// retired on-disk store left behind.
    /// </summary>
    /// <param name="isUnredirected">
    /// Whether a path is still inside the trusted scratch tree with no symlinked
    /// component along the way. Supplied by the caller because the check lives in
    /// ILD.Core, which depends on this assembly rather than the other way round;
    /// required rather than optional so a call site cannot quietly opt out of it.
    /// </param>
    public static async Task<CarryOverResult> ApplyAsync(
        AppDbContext db,
        LegacyCapture capture,
        Func<string, bool> isUnredirected,
        CancellationToken ct = default)
    {
        var carriedCount = 0;
        var missing = 0;

        if (capture.Messages.Count > 0)
        {
            var alreadyCarried = (await db.ChatAttachments.Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            var now = DateTime.UtcNow;

            foreach (var message in capture.Messages)
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

                    var content = isUnredirected(legacy.StoredPath)
                        ? await ReadBytesAsync(legacy.StoredPath, ct)
                        : null;
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

        // Only ever after a capture that is known to be whole. An incomplete one
        // means files exist that nothing has accounted for, and deleting those is
        // precisely the data loss this migration was written to prevent.
        var swept = capture.Complete ? await SweepUploadsAsync(db, isUnredirected, ct) : 0;

        return new CarryOverResult(carriedCount, missing, swept);
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
    /// The bytes, or null when the file is gone or is itself a symlink. Callers
    /// have already established that the path is unredirected; this covers the
    /// final component, which that check deliberately leaves to the opener.
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
    private static async Task<int> SweepUploadsAsync(
        AppDbContext db, Func<string, bool> isUnredirected, CancellationToken ct)
    {
        var scratchPaths = await db.ChatSessions
            .Where(s => s.ScratchPath != null && s.ScratchPath != "")
            .Select(s => s.ScratchPath!)
            .ToListAsync(ct);

        var swept = 0;
        foreach (var scratchPath in scratchPaths)
        {
            var uploads = Path.Combine(scratchPath, "uploads");

            // The recorded path is ours, but the tree it names is the agent's to
            // write in: a redirected component would aim these deletes somewhere
            // else entirely.
            if (!isUnredirected(uploads)) continue;

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
