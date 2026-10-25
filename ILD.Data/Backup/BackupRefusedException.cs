namespace ILD.Data.Backup;

/// <summary>
/// A backup that must not be restored, or a restore that must not start: the message says why
/// and is meant for the person who asked. Raised before any live data has changed.
/// </summary>
public sealed class BackupRefusedException(string message) : Exception(message)
{
    /// <summary>What a file gets that is not a backup, is cut short, or is not whole.</summary>
    public static BackupRefusedException NotABackup() => new("This is not an ILD backup, or it is damaged.");
}
