#if WORKITEM_SERVER
namespace ILD.WorkItemServer.Backup;
#else
namespace ILD.Data.Backup;
#endif

/// <summary>The names a database goes by in a backup: in its manifest and header, and in the API.</summary>
public static class ArchiveDatabase
{
    public const string Core = "core";
    public const string WorkItems = "workitems";
}

/// <summary>What a database says about itself, for deciding whether an archive may be restored into it.</summary>
/// <param name="Version">The running build's version.</param>
/// <param name="Migrations">Every migration this build knows, applied or not.</param>
/// <param name="DatabaseBytes">The database's size, or null when it cannot say.</param>
public sealed record ArchiveInfo(string Version, IReadOnlyList<string> Migrations, long? DatabaseBytes);

/// <summary>
/// One database as an archive: a manifest followed by the rows of every table, exactly as stored.
/// The archive is plain; encrypting it is the caller's business.
/// </summary>
public interface IDatabaseArchive
{
    Task<ArchiveInfo> DescribeAsync(CancellationToken cancellationToken);

    /// <param name="keyCheck">Written into the manifest as is; null when the database has none.</param>
    Task ExportAsync(Stream output, string? keyCheck, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the database's data with the archive's, in one transaction: a failure leaves the
    /// data as it was. The schema is rebuilt to the archive's migration level; the caller migrates
    /// forward afterwards. Throws <see cref="BackupRefusedException"/> for an archive that does not fit.
    /// </summary>
    Task ImportAsync(Stream input, CancellationToken cancellationToken);
}
