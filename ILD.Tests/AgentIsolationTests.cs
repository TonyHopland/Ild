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
    public void Test_run_starts_from_the_single_uid_baseline()
    {
        // The suite is written against "isolation is off unless a test turns it on
        // explicitly" (ADR-0014), but that only holds while the process-global
        // variables are absent — and a dev worktree runs INSIDE the container,
        // where the entrypoint has already exported them. TestEnvironmentBaseline
        // pins them; assert it, so a regression surfaces as this one named failure
        // instead of ~77 setpriv/permission errors spread across the adapter,
        // preview and repository suites.
        Assert.Null(AgentIsolation.AgentUser);
        Assert.Equal(
            AgentIsolation.ResolveSecretEnvironmentKeys(null).OrderBy(k => k, StringComparer.Ordinal),
            AgentIsolation.SecretEnvironmentKeys.OrderBy(k => k, StringComparer.Ordinal));

        // The two roots must be writable by whoever is running the tests rather
        // than by the orchestrator uid that provisioned the container's copies.
        foreach (var root in new[] { AgentIsolation.ScratchRoot, AgentIsolation.PrivateRoot })
        {
            var probe = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
        }
    }

    [Fact]
    public void The_privilege_drop_tool_is_an_absolute_path_everywhere()
    {
        // setpriv is the binary that performs the drop, so whoever controls its
        // resolution controls whether the drop happens at all. .NET resolves a bare
        // FileName against the CHILD environment's PATH — and both a Worktree
        // Preview's children and the orchestrator's own process PATH include an
        // agent-writable npm bin directory (ADR-0016). A planted `setpriv` there
        // would be exec'd in place of the real one, as the orchestrator, with the
        // ambient CAP_SETUID still held because nothing dropped it: an escalation
        // wearing the name of the guard against it. Pin every path that names it.
        var psi = BuildPsi();
        AgentIsolation.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");
        Assert.True(Path.IsPathRooted(psi.FileName), $"Route: '{psi.FileName}' is not absolute");

        var dropped = BuildPsi();
        AgentIsolation.DropInheritedCapabilities(dropped, agentUser: "agent");
        Assert.True(Path.IsPathRooted(dropped.FileName), $"DropInheritedCapabilities: '{dropped.FileName}' is not absolute");

        var routed = AgentIsolation.RouteCommand("claude", Array.Empty<string>(),
            agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");
        Assert.True(Path.IsPathRooted(routed.FileName), $"RouteCommand: '{routed.FileName}' is not absolute");

        // And it is where the image actually puts it, so the absolute path is not
        // merely absolute but correct.
        Assert.Equal("/usr/bin/setpriv", psi.FileName);
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

        Assert.Equal("/usr/bin/setpriv", psi.FileName);
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
    public void Route_strips_orchestrator_secrets_but_keeps_the_agent_environment()
    {
        var psi = BuildPsi();
        // An orchestrator secret .NET copied onto the psi, plus things the agent
        // legitimately needs: an adapter-set provider key (different name), the
        // git commit identity, and PATH.
        psi.Environment["ILD_DB_CONNECTION_STRING"] = "Host=postgres;Password=hunter2";
        psi.Environment["ILD_SECRET_KEY"] = "topsecret";
        psi.Environment["ILD_API_TOKEN"] = "callback-token";
        psi.Environment["ILD_PI_PROVIDER_API_KEY"] = "the-agent's-own-key";
        psi.Environment["GIT_AUTHOR_NAME"] = "ILD Agent";
        psi.Environment["PATH"] = "/usr/bin";

        AgentIsolation.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

        Assert.False(psi.Environment.ContainsKey("ILD_DB_CONNECTION_STRING"), "DB string leaked to the agent");
        Assert.False(psi.Environment.ContainsKey("ILD_SECRET_KEY"), "encryption key leaked to the agent");
        Assert.False(psi.Environment.ContainsKey("ILD_API_TOKEN"), "callback token leaked to the agent");
        // The adapter's own secret and the agent's working env must survive.
        Assert.Equal("the-agent's-own-key", psi.Environment["ILD_PI_PROVIDER_API_KEY"]);
        Assert.Equal("ILD Agent", psi.Environment["GIT_AUTHOR_NAME"]);
        Assert.Equal("/usr/bin", psi.Environment["PATH"]);
    }

    [Fact]
    public void Route_does_not_strip_secrets_when_isolation_is_off()
    {
        // Single-uid / local dev: Route is a no-op, so the env is unchanged.
        var psi = BuildPsi();
        psi.Environment["ILD_DB_CONNECTION_STRING"] = "Host=postgres";

        AgentIsolation.Route(psi, agentUser: null, agentGroup: null, agentHome: null);

        Assert.Equal("Host=postgres", psi.Environment["ILD_DB_CONNECTION_STRING"]);
    }

    [Fact]
    public void RouteCommand_neutralizes_secrets_for_the_merged_pty_environment()
    {
        // The PTY merges these overrides over the inherited env rather than
        // replacing it, so secrets are neutralized to empty rather than removed.
        var routed = AgentIsolation.RouteCommand("claude", Array.Empty<string>(),
            agentUser: "agent", agentGroup: "agent", agentHome: "/home/agent");

        Assert.Equal("/home/agent", routed.Environment["HOME"]);
        Assert.Equal(string.Empty, routed.Environment["ILD_DB_CONNECTION_STRING"]);
        Assert.Equal(string.Empty, routed.Environment["ILD_SECRET_KEY"]);
        Assert.Equal(string.Empty, routed.Environment["ILD_WORKITEM_SERVER_API_KEY"]);
    }

    [Fact]
    public void ResolveSecretEnvironmentKeys_covers_the_known_secrets_and_the_extra_denylist()
    {
        var defaults = AgentIsolation.ResolveSecretEnvironmentKeys(null);
        Assert.Contains("ILD_DB_CONNECTION_STRING", defaults);
        Assert.Contains("ILD_SECRET_KEY", defaults);
        Assert.Contains("ILD_PASSWORD", defaults);
        Assert.Contains("WORKITEM_API_KEYS", defaults);
        Assert.Contains("ILD_API_TOKEN", defaults);
        // The agent's own provider key must never be in the strip set.
        Assert.DoesNotContain("ILD_PI_PROVIDER_API_KEY", defaults);

        var extended = AgentIsolation.ResolveSecretEnvironmentKeys("MY_CUSTOM_SECRET , ANOTHER_ONE");
        Assert.Contains("MY_CUSTOM_SECRET", extended);
        Assert.Contains("ANOTHER_ONE", extended);
        Assert.Contains("ILD_DB_CONNECTION_STRING", extended); // defaults still present
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

        Assert.Equal("/usr/bin/setpriv", psi.FileName);
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

        Assert.Equal("/usr/bin/setpriv", psi.FileName);
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

        Assert.Equal("/usr/bin/setpriv", routed.FileName);
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

    [Theory]
    [InlineData("agent", "/home/agent", "/home/agent")]
    [InlineData("agent", null, null)]        // routed, but the crossing leaves HOME alone
    [InlineData("agent", "  ", null)]        // blank is unset throughout AgentIsolation
    [InlineData(null, "/home/agent", null)]  // isolation off: a configured home is inert
    public void ResolveChildHome_answers_what_the_crossing_does_to_HOME(string? user, string? home, string? expected)
    {
        // The single owner of the rule: Route applies this answer, and callers that
        // derive paths from where the child's HOME ends up (the preview's npm
        // prefix) read the same one instead of re-deriving it. The two middle cases
        // are why — a user without a home is routed but keeps the inherited HOME,
        // so anything hung off it has to fall back with it.
        Assert.Equal(expected, AgentIsolation.ResolveChildHome(user, home));
    }

    [Fact]
    public void Route_leaves_HOME_alone_when_no_agent_home_is_configured()
    {
        var psi = BuildPsi();
        psi.Environment.TryGetValue("HOME", out var originalHome);

        AgentIsolation.Route(psi, agentUser: "agent", agentGroup: "agent", agentHome: null);

        psi.Environment.TryGetValue("HOME", out var home);
        Assert.Equal(originalHome, home);
        Assert.Equal("/usr/bin/setpriv", psi.FileName);
    }

    [Fact]
    public void StripOrchestratorEnvironment_removes_every_secret_and_topology_variable()
    {
        // Seeded straight onto the psi rather than onto the process: the strip is a
        // psi transform, and ILD_AGENT_USER in particular cannot be set
        // process-wide without turning isolation on for the whole test host — the
        // thing TestEnvironmentBaseline exists to prevent. The preview-level tests
        // then prove the wiring end-to-end with variables that are inert here.
        var psi = new ProcessStartInfo("/bin/sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("env");

        var scrubbed = AgentIsolation.SecretEnvironmentKeys
            .Concat(AgentIsolation.OrchestratorTopologyEnvKeys)
            .ToArray();
        foreach (var key in scrubbed)
            psi.Environment[key] = "inherited-" + key;

        AgentIsolation.StripOrchestratorEnvironment(psi);

        Assert.Equal(Array.Empty<string>(), scrubbed.Where(psi.Environment.ContainsKey).ToArray());
    }

    [Fact]
    public void StripOrchestratorEnvironment_covers_the_nine_orchestrator_secrets()
    {
        // Pinned by name. These are the values a preview command could otherwise
        // read straight out of `env` — the DB strings, the encryption-at-rest key,
        // the bootstrap credentials and the orchestrator's own API tokens — so a
        // new one being added to the app without being added here is the
        // regression this guards.
        Assert.Equal(new[]
        {
            "ILD_AGENT_TOKEN",
            "ILD_API_TOKEN",
            "ILD_DB_CONNECTION_STRING",
            "ILD_PASSWORD",
            "ILD_SECRET_KEY",
            "ILD_USERNAME",
            "WORKITEM_API_KEYS",
            "WORKITEM_DB_CONNECTION_STRING",
            "ILD_WORKITEM_SERVER_API_KEY",
        }.OrderBy(k => k, StringComparer.Ordinal),
        AgentIsolation.SecretEnvironmentKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void StripOrchestratorEnvironment_names_the_five_topology_variables()
    {
        Assert.Equal(new[]
        {
            AgentIsolation.AgentUserEnvVar,
            AgentIsolation.AgentGroupEnvVar,
            AgentIsolation.AgentHomeEnvVar,
            AgentIsolation.ScratchRootEnvVar,
            AgentIsolation.PrivateRootEnvVar,
        }.OrderBy(k => k, StringComparer.Ordinal),
        AgentIsolation.OrchestratorTopologyEnvKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void StripOrchestratorEnvironment_leaves_everything_else_alone()
    {
        // The scrub is by exact name, not by pattern: a preview's own secrets and
        // the git commit identity travel on the same environment under different
        // names and must survive.
        var psi = new ProcessStartInfo("/bin/sh");
        psi.Environment["ILD_PI_PROVIDER_API_KEY"] = "agents-own-key";
        psi.Environment["GIT_AUTHOR_NAME"] = "ILD";
        psi.Environment["ILD_DATA_PATH"] = "/data";

        AgentIsolation.StripOrchestratorEnvironment(psi);

        Assert.Equal("agents-own-key", psi.Environment["ILD_PI_PROVIDER_API_KEY"]);
        Assert.Equal("ILD", psi.Environment["GIT_AUTHOR_NAME"]);
        Assert.Equal("/data", psi.Environment["ILD_DATA_PATH"]);
    }

    [Fact]
    public void StripOrchestratorEnvironment_does_not_change_the_command()
    {
        // It scrubs the environment and nothing else — the uid/capability decision
        // stays with Route/DropInheritedCapabilities, which callers apply
        // separately.
        var psi = new ProcessStartInfo("/bin/sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("npm run dev");

        AgentIsolation.StripOrchestratorEnvironment(psi);

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-lc", "npm run dev" }, psi.ArgumentList);
    }

    [Fact]
    public void DropInheritedCapabilities_leaves_the_environment_untouched()
    {
        // ProcessRunner (git, npm) and AIProviderService.RunShellAsync share this
        // helper, and a Cmd node may legitimately rely on the inherited
        // environment. Folding the preview's scrub in here would change both
        // silently, so the two concerns stay separate helpers (ADR-0016).
        var psi = new ProcessStartInfo("/bin/sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("git status");
        psi.Environment["ILD_DB_CONNECTION_STRING"] = "Host=db";
        psi.Environment[AgentIsolation.PrivateRootEnvVar] = "/tmp/private";

        AgentIsolation.DropInheritedCapabilities(psi, agentUser: "agent");

        Assert.Equal("Host=db", psi.Environment["ILD_DB_CONNECTION_STRING"]);
        Assert.Equal("/tmp/private", psi.Environment[AgentIsolation.PrivateRootEnvVar]);
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
