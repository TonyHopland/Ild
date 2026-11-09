using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// The agent read root holds the files ILD hands a launch to read (pi's ILD extension, the agent CLIs' MCP
/// configs), which carry the ILD API token: every level ILD creates below it is 0700 and every file 0600.
/// </summary>
public sealed class AgentReadRootTests : IDisposable
{
    private readonly string _segment = $"ild-read-root-test-{Guid.NewGuid():N}";

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(AgentIsolation.AgentReadRoot, _segment), recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void The_root_is_the_configured_path_or_a_per_user_one_under_TMPDIR()
    {
        var fallback = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"ild-agent-read-{Environment.UserName}"));

        Assert.Equal(Path.GetFullPath("/configured/read"), AgentIsolation.ResolveAgentReadRoot("/configured/read"));
        Assert.Equal(fallback, AgentIsolation.ResolveAgentReadRoot(null));
        Assert.Equal(fallback, AgentIsolation.ResolveAgentReadRoot("  "));
    }

    [Fact]
    public void Missing_levels_are_created_owner_only()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = Directory.CreateTempSubdirectory("ild-read-levels-").FullName;
        try
        {
            var root = Path.Combine(dir, "root");

            var run = AgentIsolation.CreateAgentReadDirectoryUnder(root, "ild-pi-ext", "run");

            Assert.Equal(UnixOwnership.AgentReadDirectory, UnixOwnership.PermissionsOf(root));
            Assert.Equal(UnixOwnership.AgentReadDirectory, UnixOwnership.PermissionsOf(Path.GetDirectoryName(run)!));
            Assert.Equal(UnixOwnership.AgentReadDirectory, UnixOwnership.PermissionsOf(run));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Files_are_written_owner_only_and_replace_what_was_there_whole()
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
