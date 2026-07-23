using System.Diagnostics;
using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

public class AgentUserLauncherTests
{
    private static ProcessStartInfo BuildPsi()
    {
        var psi = new ProcessStartInfo("/data/agents/claude-code/versions/v1/node_modules/.bin/claude")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = "/worktrees/wi-1",
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("do the thing");
        return psi;
    }

    [Fact]
    public void Route_is_noop_when_agent_user_is_blank()
    {
        var psi = BuildPsi();
        var originalArgs = psi.ArgumentList.ToArray();
        // Compare against the captured value rather than asserting HOME != the
        // agent home: the latter only detects a regression when the ambient HOME
        // of the test process happens to differ from it.
        psi.Environment.TryGetValue("HOME", out var originalHome);

        var routed = AgentUserLauncher.Route(psi, agentUser: null, agentGroup: null, agentHome: "/home/agent");

        Assert.Same(psi, routed);
        Assert.Equal("/data/agents/claude-code/versions/v1/node_modules/.bin/claude", psi.FileName);
        Assert.Equal(originalArgs, psi.ArgumentList);
        // HOME must not be forced when isolation is disabled.
        psi.Environment.TryGetValue("HOME", out var homeAfter);
        Assert.Equal(originalHome, homeAfter);
    }

    [Fact]
    public void Route_wraps_command_in_setpriv_dropping_all_caps()
    {
        var psi = BuildPsi();
        var innerBinary = psi.FileName;

        AgentUserLauncher.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

        Assert.Equal("setpriv", psi.FileName);
        Assert.Equal(new[]
        {
            "--reuid=agent",
            "--regid=agent",
            "--init-groups",
            "--inh-caps=-all",
            "--ambient-caps=-all",
            "--",
            innerBinary,
            "--print",
            "--",
            "do the thing",
        }, psi.ArgumentList);
    }

    [Fact]
    public void Route_sets_home_and_preserves_working_directory_and_redirects()
    {
        var psi = BuildPsi();

        AgentUserLauncher.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

        Assert.Equal("/home/agent", psi.Environment["HOME"]);
        Assert.Equal("/worktrees/wi-1", psi.WorkingDirectory);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void Route_defaults_group_to_user_and_leaves_home_untouched_when_unset()
    {
        var psi = BuildPsi();
        // psi.Environment inherits the current process env (incl. HOME) because
        // UseShellExecute is false; a null agentHome must leave it exactly as-is.
        psi.Environment.TryGetValue("HOME", out var originalHome);

        AgentUserLauncher.Route(psi, agentUser: "agent", agentGroup: null, agentHome: null);

        Assert.Equal("setpriv", psi.FileName);
        Assert.Contains("--regid=agent", psi.ArgumentList);
        psi.Environment.TryGetValue("HOME", out var homeAfter);
        Assert.Equal(originalHome, homeAfter);
    }

    [Fact]
    public void ShareScratchDirectory_is_a_noop_when_isolation_is_off()
    {
        // ILD_AGENT_USER is not set in the test environment, so the scratch dir
        // must keep the orchestrator-private mode it was created with rather
        // than being opened up to other uids.
        var dir = Path.Combine(Path.GetTempPath(), $"ild-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            if (!OperatingSystem.IsLinux()) return;
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            AgentUserLauncher.ShareScratchDirectory(dir);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Route_preserves_environment_variables_set_by_the_adapter()
    {
        var psi = BuildPsi();
        psi.Environment["OPENCODE_CONFIG_CONTENT"] = "{}";

        AgentUserLauncher.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

        // Adapter-set env must survive the wrap (setpriv passes it through).
        Assert.Equal("{}", psi.Environment["OPENCODE_CONFIG_CONTENT"]);
    }
}
