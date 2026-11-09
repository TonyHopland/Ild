using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ILD.Core.Services.RunHosts.Local;

/// <summary>
/// Reading a local worktree's <c>.git</c> pointers and links with the orchestrator's own filesystem
/// access, which is only meaningful where the worktree is on this machine. The trust check that
/// decides whether a path is a clone or worktree this orchestrator made lives here, in the
/// <c>local</c> run host, because a host whose files are elsewhere answers it differently.
/// </summary>
internal static class LocalWorktrees
{
    /// <summary>
    /// <paramref name="path"/> with every link along it replaced by what it
    /// points at, or null when it could not be resolved within
    /// <see cref="MaxLinkHops"/>. Resolved a segment at a time, because a link
    /// anywhere in the path leaves the rest of it somewhere else — a directory
    /// link is as much a way out as a file one. A segment that does not exist
    /// cannot be a link and is carried through as written, so this answers for
    /// paths about to be created too.
    /// <para>
    /// Running out of hops is a refusal, not an answer: a path still holding a
    /// link has not been followed to where it leads, and handing back where it
    /// stopped would report a chain that ends outside the worktree as sitting
    /// inside it — every hop but the last can be planted inside on purpose. The
    /// budget spans the whole path and matches what Linux itself will follow, so
    /// a chain this refuses is one the kernel would refuse to open anyway.
    /// </para>
    /// </summary>
    internal static string? ResolveLinks(string path)
    {
        var hops = 0;
        var resolved = Path.GetPathRoot(path) ?? string.Empty;
        foreach (var segment in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);
            for (var target = LinkTargetOf(resolved); target != null; target = LinkTargetOf(resolved))
            {
                if (++hops > MaxLinkHops) return null;
                resolved = Path.GetFullPath(target, Path.GetDirectoryName(resolved) ?? resolved);
            }
        }
        return resolved;
    }

    /// <summary>What <paramref name="path"/> points at, or null if it is not a link.</summary>
    internal static string? LinkTargetOf(string path) =>
        (Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path)).LinkTarget;

    /// <summary>
    /// Links followed across one path resolution, the same budget Linux spends
    /// before it gives up with <c>ELOOP</c>.
    /// </summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    /// The repository that owns <paramref name="path"/>, when <c>path/.git</c> is something this
    /// manager made: a real directory (a clone), or a regular file naming
    /// <c>&lt;repo&gt;/.git/worktrees/&lt;name&gt;</c> where <c>&lt;repo&gt;/.git</c> is a real
    /// directory and that worktree entry points back at <c>path/.git</c>. Null for anything else,
    /// links included: the agent writes these paths, and a pointer it redirected would make git
    /// open a repository, config and hooks of its choosing.
    /// </summary>
    internal static string? TrustedRepositoryRoot(string path)
    {
        var worktree = Path.GetFullPath(path);
        var dotGit = Path.Combine(worktree, ".git");
        if (LinkTargetOf(dotGit) is not null) return null;
        if (Directory.Exists(dotGit)) return worktree;
        if (!File.Exists(dotGit)) return null;

        var pointer = ReadSmallText(dotGit);
        const string prefix = "gitdir:";
        if (pointer is null || !pointer.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var entry = pointer[prefix.Length..].Trim();
        if (!Path.IsPathRooted(entry) || Path.GetFullPath(entry) != entry) return null;

        var worktrees = Path.GetDirectoryName(entry);
        var gitDir = worktrees is null ? null : Path.GetDirectoryName(worktrees);
        if (worktrees is null || gitDir is null || Path.GetFileName(worktrees) != "worktrees" || Path.GetFileName(gitDir) != ".git") return null;
        var repository = Path.GetDirectoryName(gitDir);
        if (repository is null) return null;

        if (LinkTargetOf(gitDir) is not null || !Directory.Exists(gitDir)) return null;
        if (LinkTargetOf(worktrees) is not null || LinkTargetOf(entry) is not null || !Directory.Exists(entry)) return null;

        var backlink = ReadSmallText(Path.Combine(entry, "gitdir"))?.Trim();
        if (backlink is null || !Path.IsPathRooted(backlink)) return null;
        return ResolveLinks(Path.GetFullPath(backlink)) == ResolveLinks(dotGit) ? repository : null;
    }

    private const int MaxPointerBytes = 4096;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFile(string pathname, int flags);

    // O_RDONLY | O_NONBLOCK | O_CLOEXEC. Opened without waiting, so a pipe the agent put where the
    // file should be reads as empty instead of blocking the orchestrator until something writes to it.
    private static readonly int OpenNonBlocking = OperatingSystem.IsMacOS() ? 0x4 | 0x1000000 : 0x800 | 0x80000;

    /// <summary>The first <see cref="MaxPointerBytes"/> of a regular file, or null for anything else.</summary>
    private static string? ReadSmallText(string file)
    {
        try
        {
            if (ILD.Core.Services.Implementations.Storage.FileStatus.IsSupported
                && (ILD.Core.Services.Implementations.Storage.FileStatus.TryGet(file, out var status) != 0 || status.Kind != ILD.Core.Services.Implementations.Storage.FileKind.File))
                return null;

            var descriptor = OpenFile(file, OpenNonBlocking);
            if (descriptor < 0) return null;
            using var stream = new FileStream(new SafeFileHandle(descriptor, ownsHandle: true), FileAccess.Read, bufferSize: 0);
            var buffer = new byte[MaxPointerBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read == buffer.Length ? null : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
