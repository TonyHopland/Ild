using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// The agent read root holds files the agent must read but never change (pi's ILD
/// extension, the agent CLIs' MCP configs). Each level below it is 0750 and each
/// file 0640, owned by the orchestrator.
/// </summary>
public sealed class AgentReadRootTests : IDisposable
{
    private readonly string _segment = $"ild-read-root-test-{Guid.NewGuid():N}";

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(AgentIsolation.AgentReadRoot, _segment), recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Without_uid_isolation_the_root_is_the_configured_path_or_a_per_user_one_under_TMPDIR()
    {
        // Per user: an ILD previewed inside ILD runs as the agent without the outer
        // instance's variables and must not land on the root it cannot write.
        var fallback = Path.Combine(Path.GetTempPath(), $"ild-agent-read-{Environment.UserName}");

        Assert.Equal("/configured/read", AgentIsolation.ResolveAgentReadRoot("/configured/read", agentUser: null));
        Assert.Equal(fallback, AgentIsolation.ResolveAgentReadRoot(null, agentUser: null));
        Assert.Equal(fallback, AgentIsolation.ResolveAgentReadRoot("  ", agentUser: null));
    }

    [Fact]
    public void With_uid_isolation_the_root_must_be_configured()
    {
        Assert.Equal("/configured/read", AgentIsolation.ResolveAgentReadRoot("/configured/read", "agent"));
        var error = Assert.Throws<InvalidOperationException>(() => AgentIsolation.ResolveAgentReadRoot(null, "agent"));
        Assert.Contains(AgentIsolation.AgentReadRootEnvVar, error.Message);
    }

    [Fact]
    public void A_missing_root_is_created_0750_only_without_uid_isolation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ild-read-root-missing-{Guid.NewGuid():N}");
        try
        {
            Assert.Throws<InvalidOperationException>(() => AgentIsolation.EnsureTrustedAgentReadRoot(root, "agent"));
            Assert.False(Directory.Exists(root));

            AgentIsolation.EnsureTrustedAgentReadRoot(root, agentUser: null);

            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixOwnership.AgentReadDirectory, UnixOwnership.PermissionsOf(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public void A_root_that_is_a_link_writable_by_others_or_owned_by_someone_else_is_refused()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = Directory.CreateTempSubdirectory("ild-read-root-untrusted-").FullName;
        try
        {
            var owned = Directory.CreateDirectory(Path.Combine(dir, "owned")).FullName;
            File.SetUnixFileMode(owned, UnixOwnership.AgentReadDirectory);
            AgentIsolation.EnsureTrustedAgentReadRoot(owned, "agent");

            var link = Path.Combine(dir, "link");
            Directory.CreateSymbolicLink(link, owned);
            Assert.Throws<InvalidOperationException>(() => AgentIsolation.EnsureTrustedAgentReadRoot(link, "agent"));

            var groupWritable = Directory.CreateDirectory(Path.Combine(dir, "group-writable")).FullName;
            File.SetUnixFileMode(groupWritable, UnixOwnership.AgentReadDirectory | UnixFileMode.GroupWrite);
            Assert.Throws<InvalidOperationException>(() => AgentIsolation.EnsureTrustedAgentReadRoot(groupWritable, "agent"));

            // /usr stands in for a root someone else owns; root owns everything it can chmod.
            if (Environment.UserName != "root")
                Assert.Throws<InvalidOperationException>(() => AgentIsolation.EnsureTrustedAgentReadRoot("/usr", "agent"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Every_level_it_creates_is_orchestrator_owned_and_not_group_writable()
    {
        var run = AgentIsolation.CreateAgentReadDirectory(_segment, "run");

        Assert.Equal(Path.Combine(AgentIsolation.AgentReadRoot, _segment, "run"), run);
        UnixOwnership.AssertOrchestratorOwned(Path.GetDirectoryName(run)!, UnixOwnership.AgentReadDirectory);
        UnixOwnership.AssertOrchestratorOwned(run, UnixOwnership.AgentReadDirectory);
    }

    [Fact]
    public void A_new_level_keeps_the_setgid_bit_its_parent_passes_down()
    {
        if (!OperatingSystem.IsLinux()) return;

        var parent = AgentIsolation.CreateAgentReadDirectory(_segment);
        File.SetUnixFileMode(parent, File.GetUnixFileMode(parent) | UnixFileMode.SetGroup);

        var child = AgentIsolation.CreateAgentReadDirectory(_segment, "run");

        Assert.True(File.GetUnixFileMode(child).HasFlag(UnixFileMode.SetGroup), "the shared group would stop at this level");
        Assert.Equal(UnixOwnership.AgentReadDirectory, UnixOwnership.PermissionsOf(child));
    }

    [Theory]
    [InlineData(0, "link")]
    [InlineData(0, "group-writable")]
    [InlineData(1, "link")]
    [InlineData(1, "group-writable")]
    [InlineData(2, "link")]
    [InlineData(2, "group-writable")]
    public void A_planted_link_or_group_writable_folder_at_any_level_is_refused(int level, string planted)
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = Directory.CreateTempSubdirectory("ild-read-levels-").FullName;
        try
        {
            // root, the fixed folder, the per-run folder.
            var levels = new[] { Path.Combine(dir, "root"), Path.Combine(dir, "root", "ild-pi-ext"), Path.Combine(dir, "root", "ild-pi-ext", "run") };
            for (var i = 0; i < level; i++)
                Directory.CreateDirectory(levels[i], UnixOwnership.AgentReadDirectory);
            var elsewhere = Directory.CreateDirectory(Path.Combine(dir, "elsewhere"), UnixOwnership.AgentReadDirectory).FullName;
            if (planted == "link")
            {
                Directory.CreateSymbolicLink(levels[level], elsewhere);
            }
            else
            {
                Directory.CreateDirectory(levels[level]);
                File.SetUnixFileMode(levels[level], UnixOwnership.AgentReadDirectory | UnixFileMode.GroupWrite);
            }

            Assert.Throws<InvalidOperationException>(() =>
                AgentIsolation.CreateAgentReadDirectoryUnder(levels[0], "agent", "ild-pi-ext", "run"));

            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_levels_below_a_trusted_root_are_created_0750()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = Directory.CreateTempSubdirectory("ild-read-levels-").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(dir, "root"), UnixOwnership.AgentReadDirectory).FullName;

            var run = AgentIsolation.CreateAgentReadDirectoryUnder(root, "agent", "ild-pi-ext", "run");

            Assert.Equal(UnixOwnership.AgentReadDirectory, UnixOwnership.PermissionsOf(Path.GetDirectoryName(run)!));
            Assert.Equal(UnixOwnership.AgentReadDirectory, UnixOwnership.PermissionsOf(run));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Files_are_written_0640_and_replace_what_was_there_whole()
    {
        var directory = AgentIsolation.CreateAgentReadDirectory(_segment);
        var path = Path.Combine(directory, "ild.ts");

        AgentIsolation.WriteAgentReadableFile(path, "old"u8.ToArray());
        AgentIsolation.WriteAgentReadableFile(path, "new"u8.ToArray());

        Assert.Equal("new", File.ReadAllText(path));
        UnixOwnership.AssertOrchestratorOwned(path, UnixOwnership.AgentReadFile);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_failed_write_leaves_no_temp_file()
    {
        var directory = AgentIsolation.CreateAgentReadDirectory(_segment);

        Assert.ThrowsAny<IOException>(() =>
            AgentIsolation.WriteAgentReadableFile(Path.Combine(directory, "missing", "ild.ts"), "x"u8.ToArray()));

        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
    }
}
