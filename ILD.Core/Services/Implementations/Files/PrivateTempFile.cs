namespace ILD.Core.Services.Implementations.Files;

/// <summary>
/// A scratch file only ILD's own user can read: it lives in a directory of its own, made owner-only, and is
/// created owner-only whatever the umask, so a repository's bundle is never readable from the shared temp directory.
/// Disposing deletes the file and its directory.
/// </summary>
internal sealed class PrivateTempFile : IDisposable
{
    private readonly DirectoryInfo _directory;

    private PrivateTempFile(DirectoryInfo directory)
    {
        _directory = directory;
        Path = System.IO.Path.Combine(directory.FullName, "file");
    }

    public string Path { get; }

    /// <summary>Makes the directory (mode 0700 where there are modes); the file is made by <see cref="OpenWrite"/>.</summary>
    public static PrivateTempFile Create(string prefix) => new(Directory.CreateTempSubdirectory(prefix));

    /// <summary>Creates the file, mode 0600 where there are modes, failing if it is there already.</summary>
    public FileStream OpenWrite()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path, options);
    }

    public FileStream OpenRead() => new(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

    public void Dispose()
    {
        try { _directory.Delete(recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
