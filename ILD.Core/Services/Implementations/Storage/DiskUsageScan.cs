namespace ILD.Core.Services.Implementations.Storage;

/// <summary>What a scan adds up for one entry: allocated bytes, and whether some of it could not be read.</summary>
internal sealed class UsageTally
{
    public long Bytes { get; private set; }
    public bool Incomplete { get; private set; }

    public void Add(long bytes) => Bytes += bytes;
    public void MarkIncomplete() => Incomplete = true;
}

/// <summary>
/// Allocated disk space under a set of measured paths, each inode counted once
/// however many of the paths reach it and however many hard links it has. A
/// file counts against a path's own tally only when every one of its links was
/// met under paths sharing that tally; otherwise it counts once against the
/// shared tally of the path it was first met under. So a tally covering a set of
/// worktrees ends up with exactly what removing them would free.
///
/// <para>
/// Links are never followed, a measured path that is itself a link included,
/// and a walk never enters another measured path, so nothing is counted twice.
/// One instance per measurement: it holds every hard-linked inode it meets
/// until <see cref="Run"/> returns.
/// </para>
/// </summary>
internal sealed class DiskUsageScan
{
    private sealed record Root(string Path, UsageTally Own, UsageTally Shared);

    private sealed class LinkedFile(long bytes, uint links, Root firstMet)
    {
        public long Bytes { get; } = bytes;
        public uint Links { get; } = links;
        public Root FirstMet { get; } = firstMet;
        public uint Seen { get; set; } = 1;
        public bool OneOwner { get; set; } = true;
    }

    private static readonly EnumerationOptions ChildrenOnly = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly List<Root> _roots = [];
    private readonly HashSet<(ulong, ulong)> _rootIds = [];
    private readonly HashSet<(ulong, ulong)> _visitedDirectories = [];
    private readonly Dictionary<(ulong, ulong), LinkedFile> _linkedFiles = [];

    /// <summary>
    /// Measure <paramref name="path"/> into <paramref name="own"/>. When two
    /// measured paths are the same directory, the one added first counts it, so
    /// add the more specific ones first.
    /// </summary>
    public void Add(string path, UsageTally own, UsageTally shared)
        => _roots.Add(new Root(Path.GetFullPath(path), own, shared));

    public void Run(CancellationToken cancellationToken)
    {
        var found = new List<(Root Root, FileStatusInfo Status)>();
        foreach (var root in _roots)
        {
            var errno = FileStatus.TryGet(root.Path, out var status);
            if (errno == 0)
            {
                found.Add((root, status));
                _rootIds.Add(status.Id);
            }
            else if (errno != FileStatus.ENOENT)
            {
                root.Own.MarkIncomplete();
            }
        }

        foreach (var (root, status) in found)
            Walk(root, status, cancellationToken);

        foreach (var file in _linkedFiles.Values)
        {
            if (file.OneOwner && file.Seen >= file.Links)
                file.FirstMet.Own.Add(file.Bytes);
            else
                file.FirstMet.Shared.Add(file.Bytes);
        }
    }

    private void Walk(Root root, FileStatusInfo rootStatus, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        Count(root, root.Path, rootStatus, pending);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory, "*", ChildrenOnly).ToList();
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                root.Own.MarkIncomplete();
                continue;
            }

            foreach (var child in children)
            {
                var errno = FileStatus.TryGet(child, out var status);
                if (errno == FileStatus.ENOENT)
                    continue;
                if (errno != 0)
                {
                    root.Own.MarkIncomplete();
                    continue;
                }
                if (_rootIds.Contains(status.Id))
                    continue;
                Count(root, child, status, pending);
            }
        }
    }

    private void Count(Root root, string path, FileStatusInfo status, Stack<string> pending)
    {
        if (status.Kind == FileKind.Directory)
        {
            if (!_visitedDirectories.Add(status.Id))
                return;
            root.Own.Add(status.AllocatedBytes);
            pending.Push(path);
        }
        else if (status.Links <= 1)
        {
            root.Own.Add(status.AllocatedBytes);
        }
        else if (_linkedFiles.TryGetValue(status.Id, out var file))
        {
            file.Seen++;
            if (file.FirstMet.Own != root.Own)
                file.OneOwner = false;
        }
        else
        {
            _linkedFiles[status.Id] = new LinkedFile(status.AllocatedBytes, status.Links, root);
        }
    }
}
