using ILD.Core.Services.Implementations.Files;
using ILD.Core.Services.Implementations.Storage;
using ILD.Core.Services.RunHosts.Local;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// The lasting copy of the preview data each repository keeps between runs: a folder per repository on ILD's own
/// disk, a volume per subfolder. A run never sees it. Its host fills the run's own copy from it when the run is made
/// (<see cref="PackAsync"/>) and puts that copy back when the run ends cleanly (<see cref="ReplaceAsync"/>); ILD
/// moves the data itself, never a command in a run. When runs overlap, the last to end wins.
/// </summary>
public sealed class PreviewDataStore(string root, ILogger? logger = null)
{
    /// <summary>
    /// Beside the volumes in every copy: which write of the stored copy it was taken from, so a sync-back that overwrites
    /// a copy another run put back meanwhile is told apart. Volume names cannot start with '.', so it never is one.
    /// </summary>
    internal const string GenerationFile = ".ild-generation";

    private const int MaxGenerationLength = 64;

    /// <summary>The longest archive <see cref="ReplaceAsync"/> takes: what it is given is a copy the run could have filled without end.</summary>
    public long MaxArchiveBytes { get; init; } = RunReadLimits.Default.MaxPreviewDataBytes;

    public string FolderOf(Guid repositoryId) => Path.Combine(root, repositoryId.ToString("D"));

    /// <summary>Writes the stored copy of <paramref name="repositoryId"/> to <paramref name="sink"/> as a tar archive; an empty one when nothing is stored yet.</summary>
    public Task PackAsync(Guid repositoryId, Stream sink, CancellationToken ct = default) => PackFolderAsync(FolderOf(repositoryId), sink, ct);

    /// <summary>
    /// Writes <paramref name="folder"/> to <paramref name="sink"/> as a tar archive, links as links; an empty one when there is no
    /// such folder. Refuses a folder that is itself a link, which the agent could have pointed at a folder only ILD may read.
    /// </summary>
    internal static async Task PackFolderAsync(string folder, Stream sink, CancellationToken ct = default)
    {
        var (code, error) = await RunAsync(
            """
            if [ -L "$1" ]; then echo "$1 is a link" >&2; exit 1; fi
            if [ -d "$1" ]; then exec tar -c -C "$1" .; else exec tar -c -T /dev/null; fi
            """, [folder], null, sink, ct);
        if (code != 0) throw new IOException($"could not pack {folder}: {error.Trim()}");
    }

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="folder"/>, which it makes. GNU tar refuses members that
    /// climb out with <c>..</c>, takes rooted names as relative and makes links last, so nothing lands outside it.
    /// </summary>
    internal static async Task UnpackAsync(Stream archive, string folder, CancellationToken ct = default)
    {
        var (code, error) = await RunAsync("""mkdir -p -- "$1" && exec tar -x --no-same-owner -C "$1" """, [folder], archive, null, ct);
        if (code != 0) throw new IOException($"could not unpack into {folder}: {error.Trim()}");
    }

    /// <summary>
    /// Replaces the stored copy of <paramref name="repositoryId"/> with <paramref name="archive"/>, a run's copy. It is
    /// unpacked next to the stored copy first and swapped in by renames, so an archive that is cut off or cannot be
    /// unpacked leaves the stored copy as it was. False when that happened.
    /// </summary>
    public async Task<bool> ReplaceAsync(Guid repositoryId, Stream archive, CancellationToken ct = default)
    {
        if (archive.CanSeek && archive.Length > MaxArchiveBytes)
        {
            logger?.LogWarning("The preview data a run of repository {Repository} kept is larger than the {Limit} MB ILD stores; the stored copy is unchanged", repositoryId, MaxArchiveBytes / RunReadLimits.Megabyte);
            return false;
        }

        await AgentWritableFiles.CreateDirectoryAsync(root, ct);
        var target = FolderOf(repositoryId);
        var incoming = Path.Combine(root, $".{repositoryId:D}.incoming-{Guid.NewGuid():N}");
        try
        {
            await UnpackAsync(archive, incoming, ct);
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "The preview data a run of repository {Repository} kept could not be stored; the stored copy is unchanged", repositoryId);
            await AgentWritableFiles.DeleteAsync([incoming], CancellationToken.None);
            return false;
        }

        await DropEscapingLinksAsync(incoming, repositoryId, ct);

        var generation = Path.Combine(incoming, GenerationFile);
        if (ReadGeneration(generation) != ReadGeneration(Path.Combine(target, GenerationFile)))
            logger?.LogWarning("The preview data of repository {Repository} was stored by another run after this one took its copy; this run's copy replaces it", repositoryId);
        // What the run left under the name may be a link or a folder; it is removed, not written through.
        await AgentWritableFiles.DeleteAsync([generation], ct);
        await using (var written = new FileStream(generation, FileMode.CreateNew, FileAccess.Write))
        await using (var text = new StreamWriter(written))
            await text.WriteAsync(Guid.NewGuid().ToString("N"));

        var previous = Path.Combine(root, $".{repositoryId:D}.previous-{Guid.NewGuid():N}");
        var hadPrevious = AgentWritableFiles.EntryExists(target);
        if (hadPrevious) Directory.Move(target, previous);
        try
        {
            Directory.Move(incoming, target);
        }
        catch (IOException)
        {
            if (hadPrevious) Directory.Move(previous, target);
            await AgentWritableFiles.DeleteAsync([incoming], CancellationToken.None);
            throw;
        }
        if (hadPrevious) await AgentWritableFiles.DeleteAsync([previous], CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Deletes every link under <paramref name="folder"/> that is rooted or reaches outside it, so ILD never keeps one
    /// that a later write of its own could follow. Relative links that stay inside, such as <c>node_modules/.bin</c>,
    /// are kept. Links are never followed while walking; a target that climbs back down through a name and up again
    /// (<c>x/..</c>) is dropped too, since a link at <c>x</c> would make that mean something else than it reads as.
    /// </summary>
    private async Task DropEscapingLinksAsync(string folder, Guid repositoryId, CancellationToken ct)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var pending = new Stack<string>();
        pending.Push(root);
        var doomed = new List<string>();
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is { } target)
                {
                    if (!LinkStaysInside(root, directory, target)) doomed.Add(entry.FullName);
                }
                else if (entry is DirectoryInfo child)
                {
                    pending.Push(child.FullName);
                }
            }
        }

        if (doomed.Count == 0) return;
        foreach (var path in doomed)
            logger?.LogWarning("Dropped the link {Link} from the preview data of repository {Repository}: it points outside the data", Path.GetRelativePath(root, path), repositoryId);
        await AgentWritableFiles.DeleteAsync(doomed, ct);
    }

    private static bool LinkStaysInside(string root, string directory, string target)
    {
        if (Path.IsPathRooted(target)) return false;
        var segments = target.Split('/', '\\');
        var seenName = false;
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                if (seenName) return false;
            }
            else if (segment is not ("" or ".")) seenName = true;
        }
        var resolved = Path.GetFullPath(Path.Combine(directory, target));
        return resolved == root || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>The generation in the file, read without following a link; null when there is none.</summary>
    private static string? ReadGeneration(string path)
    {
        // Only a regular file is opened: the run unpacked this, and a pipe there would hold the read for ever.
        if (FileStatus.TryGet(path, out var status) != 0 || status.Kind != FileKind.File) return null;
        if (new FileInfo(path).Length > MaxGenerationLength) return null;
        return File.ReadAllText(path).Trim();
    }

    private static async Task<(int Code, string Error)> RunAsync(string script, IReadOnlyList<string> arguments, Stream? stdin, Stream? stdout, CancellationToken ct)
    {
        using var process = LocalShell.Start(script, arguments);
        var output = stdout is null ? process.StandardOutput.ReadToEndAsync(ct) : CopyAsync(process.StandardOutput.BaseStream, stdout, ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            if (stdin is not null) await stdin.CopyToAsync(process.StandardInput.BaseStream, ct);
        }
        catch (IOException)
        {
            // The command ended without reading it all; its status says how.
        }
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        await output;
        return (process.ExitCode, await error);
    }

    private static async Task<string> CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        await from.CopyToAsync(to, ct);
        await to.FlushAsync(ct);
        return "";
    }
}
