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
    public void The_root_is_the_configured_path_or_a_fixed_one_under_TMPDIR()
    {
        Assert.Equal("/configured/read", AgentIsolation.ResolveAgentReadRoot("/configured/read"));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ild-agent-read"), AgentIsolation.ResolveAgentReadRoot(null));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ild-agent-read"), AgentIsolation.ResolveAgentReadRoot("  "));
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
