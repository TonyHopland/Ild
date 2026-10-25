using System.Buffers.Binary;
using System.Text.Json;

namespace ILD.Data.Backup;

public sealed record ArchiveTable(string Name, IReadOnlyList<string> Columns);

/// <summary>
/// The head of an archive: four bytes (big-endian) of length, then that many bytes of UTF-8 JSON.
/// </summary>
/// <param name="Database">"core" or "workitems".</param>
/// <param name="AppVersion">The build that wrote it.</param>
/// <param name="Migrations">The migrations applied to the database when it was archived.</param>
/// <param name="KeyCheck">A value only the secret key it was made with can open; null when there was no key.</param>
public sealed record ArchiveManifest(
    int Format,
    string Database,
    string AppVersion,
    IReadOnlyList<string> Migrations,
    string? KeyCheck,
    DateTime CreatedAt,
    IReadOnlyList<ArchiveTable> Tables)
{
    public const int CurrentFormat = 1;

    private const int MaxManifestBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream output, ArchiveManifest manifest, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
        await output.WriteAsync(head, ct);
        await output.WriteAsync(body, ct);
    }

    public static async Task<ArchiveManifest> ReadAsync(Stream input, CancellationToken ct)
    {
        try
        {
            var head = new byte[4];
            await input.ReadExactlyAsync(head, ct);
            var length = BinaryPrimitives.ReadInt32BigEndian(head);
            if (length is <= 0 or > MaxManifestBytes) throw BackupRefusedException.NotABackup();
            var body = new byte[length];
            await input.ReadExactlyAsync(body, ct);
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(body, Json);
            if (manifest is null || manifest.Format != CurrentFormat || manifest.Migrations is null
                || manifest.Tables is null || manifest.Database is null || manifest.AppVersion is null)
                throw BackupRefusedException.NotABackup();
            return manifest;
        }
        catch (Exception ex) when (ex is EndOfStreamException or JsonException)
        {
            throw BackupRefusedException.NotABackup();
        }
    }

    /// <summary>
    /// Refuses an archive of another database, one made by a newer build, and one whose migrations
    /// this build does not know. A version that does not parse as a number is judged by its
    /// migrations alone.
    /// </summary>
    public void RequireRestorableInto(string database, ArchiveInfo current)
    {
        if (!string.Equals(Database, database, StringComparison.Ordinal))
            throw new BackupRefusedException($"This is a backup of the '{Database}' database, not of '{database}'.");

        var unknown = Migrations.Except(current.Migrations, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new BackupRefusedException(
                $"This backup comes from a newer version of ILD: it has {unknown.Count} migration(s) this version does not know ({string.Join(", ", unknown)}). Restore it on a version at least as new.");

        if (Core(AppVersion) is { } made && Core(current.Version) is { } running && made > running)
            throw new BackupRefusedException(
                $"This backup was made by ILD {AppVersion}, which is newer than this version ({current.Version}). Restore it on a version at least as new.");
    }

    /// <summary>The numeric part of a semantic version ("1.2.3-rc.1+abc" is 1.2.3), or null.</summary>
    private static Version? Core(string version)
    {
        var end = version.IndexOfAny(['-', '+']);
        return Version.TryParse(end < 0 ? version : version[..end], out var parsed) ? parsed : null;
    }
}
