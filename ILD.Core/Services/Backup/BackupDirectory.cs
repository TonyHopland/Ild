using Microsoft.Extensions.Configuration;

namespace ILD.Core.Services.Backup;

/// <summary>
/// Where ILD keeps backup files of its own: the automatic backup a restore takes of the data it
/// replaces, and the upload a restore is working from. Only ILD's own user can read the
/// directory (0700) and the files in it (0600). It is under the data root and nowhere the web
/// server serves, and it is not a place the agent uid is given: ADR-0014 keeps the agent away
/// from the data, and a backup is all of it.
/// </summary>
public sealed class BackupDirectory
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const string PartialSuffix = ".partial";

    public BackupDirectory(IConfiguration config) => Root = RootOf(config);

    public string Root { get; }

    /// <summary>The directory for a data root configured as <c>App:DataPath</c> (set by the host at startup).</summary>
    public static string RootOf(IConfiguration config)
        => Path.Combine(
            Path.GetFullPath(config["App:DataPath"] is { Length: > 0 } path ? path : throw new InvalidOperationException("App:DataPath is not set.")),
            "backups");

    public string PreRestorePath(string database) => Path.Combine(Root, $"pre-restore-{database}.ildbackup");

    /// <summary>The automatic backup of <paramref name="database"/> taken by its last restore, or null.</summary>
    public (DateTime CreatedAt, long Bytes)? PreRestoreInfo(string database)
        => new FileInfo(PreRestorePath(database)) is { Exists: true } file ? (file.LastWriteTimeUtc, file.Length) : null;

    public FileStream? OpenPreRestore(string database)
    {
        try
        {
            return new FileStream(PreRestorePath(database), System.IO.FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A new file in the directory, removed when disposed unless it was moved into place.</summary>
    public PartialFile CreatePartial(string name)
    {
        EnsureRoot();
        var path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N") + PartialSuffix);
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode;
        return new PartialFile(path, new FileStream(path, options));
    }

    /// <summary>
    /// At startup: whatever a restore that died with the process left. Nothing else is running yet,
    /// so none of it is in use.
    /// </summary>
    public void RemoveStalePartials()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var path in Directory.EnumerateFiles(Root, "*" + PartialSuffix))
            File.Delete(path);
    }

    private void EnsureRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Root);
            return;
        }
        Directory.CreateDirectory(Root, DirectoryMode);
        File.SetUnixFileMode(Root, DirectoryMode);
    }

    public sealed class PartialFile(string path, FileStream stream) : IAsyncDisposable
    {
        private bool _moved;

        public string Path => path;
        public FileStream Stream => stream;

        /// <summary>Replaces <paramref name="target"/> with this file, which is then no longer removed on disposal.</summary>
        public async Task MoveToAsync(string target)
        {
            await stream.FlushAsync();
            await stream.DisposeAsync();
            File.Move(path, target, overwrite: true);
            _moved = true;
        }

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            if (!_moved) File.Delete(path);
        }
    }
}
