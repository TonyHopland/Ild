using System.Text;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// <see cref="AgentIsolation.WriteSharedFile"/> and
/// <see cref="AgentIsolation.DeleteScratchDirectory"/> work by path inside scratch
/// the agent can also write, so the agent may have planted a symlink at the
/// target or swapped one in for a directory on the way to it.
/// </summary>
public sealed class AgentIsolationSharedFileTests : IDisposable
{
    private readonly string _dir;

    public AgentIsolationSharedFileTests()
    {
        _dir = Path.Combine(AgentIsolation.ScratchRoot, $"ild-shared-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Writes_the_content_without_group_or_other_write()
    {
        var path = Path.Combine(_dir, "ild.ts");

        AgentIsolation.WriteSharedFile(path, Write("ours"));

        Assert.Equal("ours", File.ReadAllText(path));
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(path);
            Assert.False(mode.HasFlag(UnixFileMode.GroupWrite), "the agent could rewrite the file");
            Assert.False(mode.HasFlag(UnixFileMode.OtherWrite), "anyone could rewrite the file");
            Assert.True(mode.HasFlag(UnixFileMode.GroupRead), "the agent must still read the file");
        }
        AssertNoTempFileLeft();
    }

    [Fact]
    public void Replaces_an_existing_file()
    {
        var path = Path.Combine(_dir, "ild.ts");
        File.WriteAllText(path, "old");

        AgentIsolation.WriteSharedFile(path, Write("new"));

        Assert.Equal("new", File.ReadAllText(path));
        AssertNoTempFileLeft();
    }

    [Fact]
    public void Replaces_a_planted_symlink_instead_of_writing_through_it()
    {
        var victim = Path.Combine(_dir, "orchestrator-owned.txt");
        File.WriteAllText(victim, "victim");
        var path = Path.Combine(_dir, "ild.ts");
        File.CreateSymbolicLink(path, victim);

        AgentIsolation.WriteSharedFile(path, Write("ours"));

        Assert.Equal("victim", File.ReadAllText(victim));
        Assert.Null(new FileInfo(path).LinkTarget);
        Assert.Equal("ours", File.ReadAllText(path));
        AssertNoTempFileLeft();
    }

    [Fact]
    public void Never_creates_the_target_of_a_dangling_symlink()
    {
        var victim = Path.Combine(_dir, "not-yet-there.txt");
        var path = Path.Combine(_dir, "ild.ts");
        File.CreateSymbolicLink(path, victim);

        Assert.ThrowsAny<IOException>(() => AgentIsolation.WriteSharedFile(path, Write("ours")));

        Assert.False(File.Exists(victim), "the write followed the planted link");
        AssertNoTempFileLeft();
    }

    [Fact]
    public void Refuses_to_write_through_a_symlinked_directory()
    {
        var victim = Directory.CreateDirectory(Path.Combine(_dir, "orchestrator-owned")).FullName;
        var link = Path.Combine(_dir, "ild-pi-ext");
        Directory.CreateSymbolicLink(link, victim);

        Assert.ThrowsAny<IOException>(() => AgentIsolation.WriteSharedFile(Path.Combine(link, "ild.ts"), Write("ours")));

        Assert.Empty(Directory.GetFileSystemEntries(victim));
    }

    [Fact]
    public void A_failed_write_leaves_the_target_untouched_and_no_temp_file()
    {
        var path = Path.Combine(_dir, "ild.ts");
        File.WriteAllText(path, "old");

        Assert.Throws<InvalidOperationException>(() =>
            AgentIsolation.WriteSharedFile(path, _ => throw new InvalidOperationException("boom")));

        Assert.Equal("old", File.ReadAllText(path));
        AssertNoTempFileLeft();
    }

    [Fact]
    public void DeleteScratchDirectory_removes_a_plain_tree_and_ignores_a_missing_one()
    {
        var tree = Path.Combine(_dir, "ild-pi-ext", "run");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "ild.ts"), "token");

        AgentIsolation.DeleteScratchDirectory(ScratchSegments(tree));
        AgentIsolation.DeleteScratchDirectory(ScratchSegments(tree));

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void DeleteScratchDirectory_refuses_to_delete_through_a_symlinked_directory()
    {
        var victim = Path.Combine(_dir, "orchestrator-owned", "run");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "ild-pi-ext"), Path.Combine(_dir, "orchestrator-owned"));

        Assert.ThrowsAny<IOException>(() =>
            AgentIsolation.DeleteScratchDirectory(ScratchSegments(Path.Combine(_dir, "ild-pi-ext", "run"))));

        Assert.True(File.Exists(Path.Combine(victim, "keep.txt")), "the delete followed the planted link");
    }

    [Fact]
    public void HasNoLinkBelowScratchRoot_rejects_a_link_anywhere_on_the_path_and_paths_outside_scratch()
    {
        var real = Directory.CreateDirectory(Path.Combine(_dir, "real")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_dir, "link"), real);

        Assert.True(AgentIsolation.HasNoLinkBelowScratchRoot(Path.Combine(real, "ild.ts")));
        Assert.True(AgentIsolation.HasNoLinkBelowScratchRoot(Path.Combine(_dir, "missing", "ild.ts")));
        Assert.False(AgentIsolation.HasNoLinkBelowScratchRoot(Path.Combine(_dir, "link", "ild.ts")));
        Assert.False(AgentIsolation.HasNoLinkBelowScratchRoot(Path.Combine(_dir, "link")));
        Assert.Throws<ArgumentException>(() => AgentIsolation.HasNoLinkBelowScratchRoot("/definitely/not/scratch/ild.ts"));
    }

    private static string[] ScratchSegments(string path)
        => Path.GetRelativePath(AgentIsolation.ScratchRoot, path).Split(Path.DirectorySeparatorChar);

    private static Action<Stream> Write(string content)
        => stream => stream.Write(Encoding.UTF8.GetBytes(content));

    private void AssertNoTempFileLeft()
        => Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
}
