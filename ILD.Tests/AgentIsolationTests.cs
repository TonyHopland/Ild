using System.Diagnostics;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class AgentIsolationTests
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

        var routed = AgentIsolation.Route(psi, agentUser: null, agentGroup: null, agentHome: "/home/agent");

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

        AgentIsolation.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

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

        AgentIsolation.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

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

        AgentIsolation.Route(psi, agentUser: "agent", agentGroup: null, agentHome: null);

        Assert.Equal("setpriv", psi.FileName);
        Assert.Contains("--regid=agent", psi.ArgumentList);
        psi.Environment.TryGetValue("HOME", out var homeAfter);
        Assert.Equal(originalHome, homeAfter);
    }

    [Fact]
    public void CreateScratchDirectory_roots_scratch_at_the_scratch_root()
    {
        // Scratch the two uids share must land under ScratchRoot — under isolation
        // that is a setgid shared-group tree, and it is the inheritance from it
        // (not a per-directory chmod) that makes an orchestrator-seeded file stay
        // writable by the agent.
        var created = AgentIsolation.CreateScratchDirectory("ild-scratch-test", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(Directory.Exists(created));
            Assert.StartsWith(
                Path.TrimEndingDirectorySeparator(AgentIsolation.ScratchRoot),
                created,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(created, recursive: true);
        }
    }

    [Fact]
    public void PrivateRoot_is_always_absolute()
    {
        // This path is handed to git as GIT_ASKPASS, and git runs with the worktree
        // as its cwd — a relative root resolves inside the worktree, so git dies
        // with "cannot exec" and every authenticated clone/fetch/push fails. The
        // regression came from a root that was only absolute when ILD_DATA_PATH
        // happened to be set, so assert it holds whatever the environment says.
        Assert.True(Path.IsPathRooted(AgentIsolation.PrivateRoot),
            $"PrivateRoot must be absolute, was '{AgentIsolation.PrivateRoot}'");

        // Reading only the ambient environment would exercise the (already
        // rooted) fallback and never the configured branch that actually broke,
        // so drive the resolver directly with a relative value.
        var fromRelative = AgentIsolation.ResolvePrivateRoot("relative/priv");
        Assert.True(Path.IsPathRooted(fromRelative),
            $"a configured relative root must still resolve absolute, was '{fromRelative}'");
        Assert.True(Path.IsPathRooted(AgentIsolation.ResolvePrivateRoot(null)));
    }

    [Fact]
    public void CreatePrivateDirectory_makes_the_root_owner_only()
    {
        // "Private" has to be a property of the root itself, not something each
        // caller remembers to apply to its own files: the agent can traverse to a
        // known path, so a group/other-readable root exposes everything beneath it.
        if (!OperatingSystem.IsLinux()) return;

        var created = AgentIsolation.CreatePrivateDirectory("ild-private-test", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(Directory.Exists(created));
            Assert.StartsWith(
                Path.TrimEndingDirectorySeparator(AgentIsolation.PrivateRoot),
                created,
                StringComparison.Ordinal);

            var mode = File.GetUnixFileMode(AgentIsolation.PrivateRoot);
            Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
            Assert.False(mode.HasFlag(UnixFileMode.GroupExecute));
            Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
            Assert.False(mode.HasFlag(UnixFileMode.OtherExecute));
            Assert.True(mode.HasFlag(UnixFileMode.UserRead));
            Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }
        finally
        {
            Directory.Delete(created, recursive: true);
        }
    }

    [Fact]
    public void ScratchRoot_is_the_configured_value_or_tmpdir()
    {
        // Drive the resolver directly rather than reading the ambient env var and
        // re-deriving the expected value with the implementation's own formula —
        // that would be tautological for the "configured wins" branch and would
        // never exercise it in CI (var unset). The explicit-parameter seam also
        // avoids setting the process-global var, which PiAdapter reads live and
        // xUnit parallelism would race. Mirrors PrivateRoot_is_always_absolute.
        Assert.Equal("/configured/scratch", AgentIsolation.ResolveScratchRoot("/configured/scratch"));
        Assert.Equal(Path.GetTempPath(), AgentIsolation.ResolveScratchRoot(null));
        Assert.Equal(Path.GetTempPath(), AgentIsolation.ResolveScratchRoot("   "));
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

            AgentIsolation.ProtectFromAgentWrites(root, agentUser: "agent");

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
    public void StageForAgentExec_closes_the_directory_until_published()
    {
        // The abandon path is the whole justification for the scope shape: a
        // failed install must leave the tree closed, not half-open. Nothing else
        // asserts it — the managed-agent failure test deletes the version dir.
        if (!OperatingSystem.IsLinux()) return;

        var dir = Path.Combine(Path.GetTempPath(), $"ild-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.SetGroup);
        try
        {
            using (AgentIsolation.StageForAgentExec(dir, agentUser: "agent"))
            {
                // Left without Publish() — an abandoned/failed install.
                var mode = File.GetUnixFileMode(dir);
                Assert.False(mode.HasFlag(UnixFileMode.GroupRead), "agent could read the half-built tree");
                Assert.False(mode.HasFlag(UnixFileMode.GroupExecute), "agent could traverse the half-built tree");
                Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
                Assert.False(mode.HasFlag(UnixFileMode.OtherExecute));
            }

            // Dispose does not reopen it.
            var afterDispose = File.GetUnixFileMode(dir);
            Assert.False(afterDispose.HasFlag(UnixFileMode.GroupExecute), "an abandoned stage must stay closed");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StageForAgentExec_publish_never_leaves_the_tree_agent_writable()
    {
        // Regression guard for the clamp: the captured mode is whatever the dir had
        // when staging opened, which under the container's umask 002 + setgid parent
        // is group-writable (2775). Publish must not restore that write bit — the
        // tree `current` is about to name would be agent-writable. This distinction
        // never arises under the test process's umask 022, so it is set explicitly.
        if (!OperatingSystem.IsLinux()) return;

        var dir = Path.Combine(Path.GetTempPath(), $"ild-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        // 2775: group-writable + setgid, exactly what a mid-install dir carries.
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            | UnixFileMode.SetGroup);
        try
        {
            var staged = AgentIsolation.StageForAgentExec(dir, agentUser: "agent");
            staged.Publish();

            var mode = File.GetUnixFileMode(dir);
            Assert.False(mode.HasFlag(UnixFileMode.GroupWrite), "Publish re-granted group write");
            Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
            // But it must still be reachable and executable, and keep the shared
            // group via setgid, or it drifts out of the shell-side scheme.
            Assert.True(mode.HasFlag(UnixFileMode.GroupRead));
            Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
            Assert.True(mode.HasFlag(UnixFileMode.SetGroup), "Publish dropped setgid");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
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

            AgentIsolation.ProtectFromAgentWrites(dir, agentUser: null);

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

        AgentIsolation.DropInheritedCapabilities(psi, agentUser: "agent");

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

        AgentIsolation.DropInheritedCapabilities(psi, agentUser: null);

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-lc", "npm run dev" }, psi.ArgumentList);
    }

    [Fact]
    public void RouteCommand_wraps_a_pty_launch_to_the_agent_uid()
    {
        var routed = AgentIsolation.RouteCommand("/data/agents/claude-code/bin/claude",
            Array.Empty<string>(), agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

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
    public void RouteCommand_carries_the_agent_home_with_the_command()
    {
        // Setting HOME is half of crossing to the agent uid, so it travels WITH
        // the routed command. If a caller could get the setpriv wrap without it,
        // the login TUI would write credentials into the orchestrator's home and
        // every later run would read as logged-out.
        var routed = AgentIsolation.RouteCommand("claude", Array.Empty<string>(),
            agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

        Assert.Equal("/home/agent", routed.Environment["HOME"]);
    }

    [Fact]
    public void RouteCommand_carries_no_environment_when_isolation_is_off()
    {
        var routed = AgentIsolation.RouteCommand("claude", Array.Empty<string>(),
            agentUser: null, agentGroup: null, agentHome: "/home/agent");

        Assert.Empty(routed.Environment);
    }

    [Fact]
    public void RouteCommand_is_a_noop_when_isolation_is_off()
    {
        var routed = AgentIsolation.RouteCommand("claude", new[] { "--version" },
            agentUser: null, agentGroup: null, agentHome: null);

        Assert.Equal("claude", routed.FileName);
        Assert.Equal(new[] { "--version" }, routed.Arguments);
    }

    [Fact]
    public void Wrap_rejects_the_legacy_arguments_string()
    {
        // Arguments and ArgumentList are mutually exclusive; moving FileName into
        // the argv would leave a non-empty Arguments applying to setpriv instead
        // of the real command. No caller uses it — fail loudly if one starts to.
        var psi = new ProcessStartInfo("claude") { Arguments = "--print hello" };

        Assert.Throws<InvalidOperationException>(
            () => AgentIsolation.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: null));
    }

    [Fact]
    public void Route_preserves_environment_variables_set_by_the_adapter()
    {
        var psi = BuildPsi();
        psi.Environment["OPENCODE_CONFIG_CONTENT"] = "{}";

        AgentIsolation.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

        // Adapter-set env must survive the wrap (setpriv passes it through).
        Assert.Equal("{}", psi.Environment["OPENCODE_CONFIG_CONTENT"]);
    }
}
