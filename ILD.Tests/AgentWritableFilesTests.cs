using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// <see cref="AgentWritableFiles"/> touches paths the agent can write, so whatever
/// the agent planted there — a link, a directory, a read-only folder — must be
/// replaced or cleared, never followed, and must never make the operation fail.
/// Here the "agent" is the test user, since uid isolation is off in tests; the
/// crossing itself is checked on the start info it builds.
/// </summary>
public sealed class AgentWritableFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-agent-files-").FullName;

    public void Dispose()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var directory in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.UserWrite); } catch { /* best effort */ }
            }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Commands_cross_to_the_agent_uid_when_isolation_is_on()
    {
        var psi = AgentWritableFiles.BuildStartInfo("true", ["/some/path"], "agent", "agent", "/home/agent");

        Assert.Equal("/usr/bin/setpriv", psi.FileName);
        Assert.Contains("/bin/sh", psi.ArgumentList);
        Assert.Contains("/some/path", psi.ArgumentList);
    }

    [Fact]
    public void Commands_run_directly_when_isolation_is_off()
    {
        var psi = AgentWritableFiles.BuildStartInfo("true", ["/some/path"], agentUser: null, agentGroup: null, agentHome: null);

        Assert.Equal("/bin/sh", psi.FileName);
    }

    [Fact]
    public async Task CreateDirectoryAsync_creates_every_missing_level()
    {
        var path = Path.Combine(_dir, "a", "b");

        await AgentWritableFiles.CreateDirectoryAsync(path);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task WriteFileAsync_replaces_a_planted_link_without_touching_its_target()
    {
        var victim = Path.Combine(_dir, "orchestrator-owned.txt");
        File.WriteAllText(victim, "victim");
        var path = Path.Combine(_dir, "models.json");
        File.CreateSymbolicLink(path, victim);

        await AgentWritableFiles.WriteFileAsync(path, "ours");

        Assert.Equal("victim", File.ReadAllText(victim));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal("ours", File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteFileAsync_replaces_a_planted_read_only_directory()
    {
        var path = Path.Combine(_dir, "models.json");
        PlantReadOnlyFolder(path);

        await AgentWritableFiles.WriteFileAsync(path, "ours");

        Assert.Equal("ours", File.ReadAllText(path));
    }

    [Fact]
    public async Task CreateDirectoryAsync_replaces_a_planted_file_or_dangling_link_at_the_path_or_on_the_way()
    {
        var fileAtPath = Path.Combine(_dir, "file-at-path");
        File.WriteAllText(fileAtPath, "x");
        var linkAtPath = Path.Combine(_dir, "link-at-path");
        File.CreateSymbolicLink(linkAtPath, Path.Combine(_dir, "nowhere"));
        var fileOnTheWay = Path.Combine(_dir, "file-on-the-way");
        File.WriteAllText(fileOnTheWay, "x");

        await AgentWritableFiles.CreateDirectoryAsync(fileAtPath);
        await AgentWritableFiles.CreateDirectoryAsync(linkAtPath);
        await AgentWritableFiles.CreateDirectoryAsync(Path.Combine(fileOnTheWay, "run"));

        Assert.True(Directory.Exists(fileAtPath));
        Assert.Null(new DirectoryInfo(linkAtPath).LinkTarget);
        Assert.True(Directory.Exists(linkAtPath));
        Assert.True(Directory.Exists(Path.Combine(fileOnTheWay, "run")));
    }

    [Fact]
    public async Task CreateDirectoryAsync_keeps_a_link_to_a_directory_on_the_way()
    {
        var real = Directory.CreateDirectory(Path.Combine(_dir, "store")).FullName;
        var link = Path.Combine(_dir, ".claude");
        Directory.CreateSymbolicLink(link, real);

        await AgentWritableFiles.CreateDirectoryAsync(Path.Combine(link, "projects", "wt"));

        Assert.Equal(real, new DirectoryInfo(link).LinkTarget);
        Assert.True(Directory.Exists(Path.Combine(real, "projects", "wt")));
    }

    [Fact]
    public async Task CreateDirectoryAsync_leaves_a_read_only_directory_on_the_way_writable()
    {
        if (!OperatingSystem.IsLinux()) return;

        var parent = Directory.CreateDirectory(Path.Combine(_dir, "ild-pi-agent")).FullName;
        var existing = Directory.CreateDirectory(Path.Combine(parent, "run-1")).FullName;
        File.SetUnixFileMode(existing, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await AgentWritableFiles.CreateDirectoryAsync(existing);
        await AgentWritableFiles.CreateDirectoryAsync(Path.Combine(parent, "run-2"));

        Assert.True(File.GetUnixFileMode(existing).HasFlag(UnixFileMode.UserWrite));
        Assert.True(Directory.Exists(Path.Combine(parent, "run-2")));
    }

    [Fact]
    public async Task FileExistsAsync_is_true_only_for_a_regular_file()
    {
        var file = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(file, "x");
        var link = Path.Combine(_dir, "link.jsonl");
        File.CreateSymbolicLink(link, file);
        var directory = Directory.CreateDirectory(Path.Combine(_dir, "folder.jsonl")).FullName;

        Assert.True(await AgentWritableFiles.FileExistsAsync(file));
        Assert.False(await AgentWritableFiles.FileExistsAsync(link));
        Assert.False(await AgentWritableFiles.FileExistsAsync(directory));
        Assert.False(await AgentWritableFiles.FileExistsAsync(Path.Combine(_dir, "missing.jsonl")));
    }

    [Fact]
    public async Task ReadFileAsync_reads_a_regular_file_but_never_through_a_link()
    {
        var file = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(file, "{\"type\":\"session\"}\n");
        var link = Path.Combine(_dir, "other.jsonl");
        File.CreateSymbolicLink(link, file);

        Assert.Equal("{\"type\":\"session\"}\n", await AgentWritableFiles.ReadFileAsync(file));
        Assert.Null(await AgentWritableFiles.ReadFileAsync(link));
        Assert.Null(await AgentWritableFiles.ReadFileAsync(Path.Combine(_dir, "missing.jsonl")));
    }

    [Fact]
    public async Task ListFilesAsync_lists_regular_files_below_with_their_first_line_and_skips_links()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_dir, "nested")).FullName;
        File.WriteAllText(Path.Combine(_dir, "a.jsonl"), "first a\nsecond a\n");
        File.WriteAllText(Path.Combine(nested, "b.jsonl"), "first b\n");
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "not a match\n");
        File.CreateSymbolicLink(Path.Combine(_dir, "link.jsonl"), Path.Combine(_dir, "a.jsonl"));

        var files = await AgentWritableFiles.ListFilesAsync(_dir, "*.jsonl");

        Assert.Equal(
            new[] { (Path.Combine(_dir, "a.jsonl"), "first a"), (Path.Combine(nested, "b.jsonl"), "first b") },
            files.OrderBy(f => f.Path, StringComparer.Ordinal));
        Assert.Empty(await AgentWritableFiles.ListFilesAsync(Path.Combine(_dir, "missing"), "*.jsonl"));
    }

    [Fact]
    public async Task DeleteAsync_clears_trees_with_read_only_folders_and_unlinks_links_without_following_them()
    {
        var tree = Path.Combine(_dir, "agent-dir");
        PlantReadOnlyFolder(Path.Combine(tree, "extensions", "ild.ts"));
        var victim = Directory.CreateDirectory(Path.Combine(_dir, "orchestrator-owned")).FullName;
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
        var link = Path.Combine(_dir, "session-dir");
        Directory.CreateSymbolicLink(link, victim);

        Assert.True(await AgentWritableFiles.DeleteAsync([tree, link, Path.Combine(_dir, "missing")]));

        Assert.False(Directory.Exists(tree));
        Assert.False(Path.Exists(link));
        Assert.True(File.Exists(Path.Combine(victim, "keep.txt")));
    }

    [Fact]
    public async Task DeleteInSubdirectoriesAsync_removes_the_path_in_every_subdirectory_and_nothing_else()
    {
        var first = Path.Combine(_dir, "run-1");
        var second = Path.Combine(_dir, "run-2");
        Directory.CreateDirectory(Path.Combine(first, "extensions"));
        File.WriteAllText(Path.Combine(first, "extensions", "ild.ts"), "old token");
        File.WriteAllText(Path.Combine(first, "models.json"), "{}");
        PlantReadOnlyFolder(Path.Combine(second, "extensions", "ild.ts"));

        Assert.True(await AgentWritableFiles.DeleteInSubdirectoriesAsync(_dir, "extensions/ild.ts"));

        Assert.False(Path.Exists(Path.Combine(first, "extensions", "ild.ts")));
        Assert.False(Path.Exists(Path.Combine(second, "extensions", "ild.ts")));
        Assert.True(File.Exists(Path.Combine(first, "models.json")));
    }

    [Fact]
    public async Task DeleteInSubdirectoriesAsync_is_fine_with_a_missing_or_empty_directory()
    {
        Assert.True(await AgentWritableFiles.DeleteInSubdirectoriesAsync(Path.Combine(_dir, "missing"), "extensions/ild.ts"));
        Assert.True(await AgentWritableFiles.DeleteInSubdirectoriesAsync(_dir, "extensions/ild.ts"));
    }

    /// <summary>A directory at <paramref name="path"/> with a read-only folder inside, as an agent could leave.</summary>
    private static void PlantReadOnlyFolder(string path)
    {
        var locked = Path.Combine(path, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "file"), "x");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }
}
