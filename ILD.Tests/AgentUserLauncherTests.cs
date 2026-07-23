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
    public void ShareScratchDirectory_grants_world_write_with_sticky_bit_when_isolation_is_on()
    {
        // The isolation-ON path is the one that widens a mode, so assert the
        // granted bits directly: the agent runs as another uid and the scratch
        // dir sits outside any shared-group tree, so it must be world-writable —
        // and sticky, so neither uid can remove the other's files.
        if (!OperatingSystem.IsLinux()) return;

        var dir = Path.Combine(Path.GetTempPath(), $"ild-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            AgentUserLauncher.ShareScratchDirectory(dir, agentUser: "agent");

            var mode = File.GetUnixFileMode(dir);
            Assert.True(mode.HasFlag(UnixFileMode.OtherWrite), "agent (a different uid) must be able to write");
            Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
            Assert.True(mode.HasFlag(UnixFileMode.GroupWrite));
            Assert.True(mode.HasFlag(UnixFileMode.StickyBit), "sticky bit stops either uid deleting the other's files");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProtectFromAgentWrites_strips_group_and_other_write_across_the_tree()
    {
        // Mirrors what npm leaves behind after a runtime agent install under the
        // container's umask 002: the agent must still be able to exec the binary
        // but must not be able to rewrite it.
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), $"ild-install-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "node_modules", ".bin");
        Directory.CreateDirectory(nested);
        var binary = Path.Combine(nested, "cli");
        File.WriteAllText(binary, "#!/bin/sh\n");
        try
        {
            const UnixFileMode groupWritableExecutable =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(binary, groupWritableExecutable);
            File.SetUnixFileMode(nested, groupWritableExecutable);
            File.SetUnixFileMode(root, groupWritableExecutable);

            AgentUserLauncher.ProtectFromAgentWrites(root, agentUser: "agent");

            foreach (var path in new[] { root, nested, binary })
            {
                var mode = File.GetUnixFileMode(path);
                Assert.False(mode.HasFlag(UnixFileMode.GroupWrite), $"{path} stayed group-writable");
                Assert.False(mode.HasFlag(UnixFileMode.OtherWrite), $"{path} stayed other-writable");
                // Read/execute must survive — the agent still has to run the CLI.
                Assert.True(mode.HasFlag(UnixFileMode.GroupRead));
                Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
                Assert.True(mode.HasFlag(UnixFileMode.UserWrite), "the owning orchestrator keeps write");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProtectFromAgentWrites_is_a_noop_when_isolation_is_off()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = Path.Combine(Path.GetTempPath(), $"ild-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            const UnixFileMode groupWritable =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(dir, groupWritable);

            AgentUserLauncher.ProtectFromAgentWrites(dir, agentUser: null);

            Assert.Equal(groupWritable, File.GetUnixFileMode(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DropInheritedCapabilities_wraps_without_changing_uid()
    {
        var psi = new ProcessStartInfo("/bin/sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("npm run dev");

        AgentUserLauncher.DropInheritedCapabilities(psi, agentUser: "agent");

        Assert.Equal("setpriv", psi.FileName);
        Assert.Equal(new[]
        {
            "--inh-caps=-all",
            "--ambient-caps=-all",
            "--",
            "/bin/sh",
            "-lc",
            "npm run dev",
        }, psi.ArgumentList);
        // Crucially no --reuid: this stays the orchestrator's uid, it only sheds
        // the ambient capabilities it would otherwise pass to agent-authored code.
        Assert.DoesNotContain(psi.ArgumentList, a => a.StartsWith("--reuid", StringComparison.Ordinal));
    }

    [Fact]
    public void DropInheritedCapabilities_is_a_noop_when_isolation_is_off()
    {
        var psi = new ProcessStartInfo("/bin/sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("npm run dev");

        AgentUserLauncher.DropInheritedCapabilities(psi, agentUser: null);

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-lc", "npm run dev" }, psi.ArgumentList);
    }

    [Fact]
    public void RouteCommand_wraps_a_pty_launch_to_the_agent_uid()
    {
        var routed = AgentUserLauncher.RouteCommand("/data/agents/claude-code/bin/claude",
            Array.Empty<string>(), agentUser: "agent", agentGroup: "agent");

        Assert.Equal("setpriv", routed.FileName);
        Assert.Equal(new[]
        {
            "--reuid=agent",
            "--regid=agent",
            "--init-groups",
            "--inh-caps=-all",
            "--ambient-caps=-all",
            "--",
            "/data/agents/claude-code/bin/claude",
        }, routed.Arguments);
    }

    [Fact]
    public void RouteCommand_is_a_noop_when_isolation_is_off()
    {
        var routed = AgentUserLauncher.RouteCommand("claude", new[] { "--version" },
            agentUser: null, agentGroup: null);

        Assert.Equal("claude", routed.FileName);
        Assert.Equal(new[] { "--version" }, routed.Arguments);
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
