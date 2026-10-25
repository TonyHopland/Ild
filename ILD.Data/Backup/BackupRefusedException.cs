namespace ILD.Data.Backup;

/// <summary>
/// A backup that must not be restored, or a restore that must not start: the message says why
/// and is meant for the person who asked. Raised before any live data has changed.
/// </summary>
public sealed class BackupRefusedException(string message) : Exception(message);
