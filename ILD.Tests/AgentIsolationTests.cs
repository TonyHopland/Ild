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
    public void Test_run_starts_from_its_own_baseline()
    {
        Assert.Equal(
            AgentIsolation.ResolveSecretEnvironmentKeys(null).OrderBy(k => k, StringComparer.Ordinal),
            AgentIsolation.SecretEnvironmentKeys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var root in new[] { AgentIsolation.ScratchRoot, AgentIsolation.PrivateRoot })
        {
            var probe = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
        }
    }

    [Fact]
    public void ApplyLaunchEnvironment_leaves_the_command_alone()
    {
        var psi = BuildPsi();
        var originalArgs = psi.ArgumentList.ToArray();
        psi.Environment.TryGetValue("HOME", out var originalHome);

        var prepared = AgentIsolation.ApplyLaunchEnvironment(psi, egressProxy: null);

        Assert.Same(psi, prepared);
        Assert.Equal("/data/agents/claude-code/versions/v1/node_modules/.bin/claude", psi.FileName);
        Assert.Equal(originalArgs, psi.ArgumentList);
        Assert.Equal("/worktrees/wi-1", psi.WorkingDirectory);
        psi.Environment.TryGetValue("HOME", out var homeAfter);
        Assert.Equal(originalHome, homeAfter);
    }

    private static readonly string[] BuildServerVariables =
        ["MSBUILDDISABLENODEREUSE", "UseSharedCompilation", "DOTNET_CLI_USE_MSBUILD_SERVER"];

    [Theory]
    [InlineData("http://127.0.0.1:3128")]
    [InlineData(null)]
    public void A_launch_keeps_build_servers_from_outliving_it(string? egressProxy)
    {
        var psi = BuildPsi();
        foreach (var name in BuildServerVariables)
            psi.Environment.Remove(name);

        AgentIsolation.ApplyLaunchEnvironment(psi, egressProxy);

        Assert.Equal("1", psi.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("false", psi.Environment["UseSharedCompilation"]);
        Assert.Equal("0", psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
    }

    [Fact]
    public void A_launch_keeps_a_build_server_setting_it_already_has()
    {
        var psi = BuildPsi();
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "0";
        psi.Environment["UseSharedCompilation"] = "true";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "1";

        AgentIsolation.ApplyLaunchEnvironment(psi, egressProxy: null);

        Assert.Equal("0", psi.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("true", psi.Environment["UseSharedCompilation"]);
        Assert.Equal("1", psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
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
        Assert.Contains("ILD_DB_CONNECTION_STRING", extended);
    }

    [Fact]
    public void CreateScratchDirectory_roots_scratch_at_the_scratch_root()
    {
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
        // with "cannot exec" and every authenticated clone/fetch/push fails.
        Assert.True(Path.IsPathRooted(AgentIsolation.PrivateRoot),
            $"PrivateRoot must be absolute, was '{AgentIsolation.PrivateRoot}'");

        var fromRelative = AgentIsolation.ResolvePrivateRoot("relative/priv");
        Assert.True(Path.IsPathRooted(fromRelative),
            $"a configured relative root must still resolve absolute, was '{fromRelative}'");
        Assert.True(Path.IsPathRooted(AgentIsolation.ResolvePrivateRoot(null)));
    }

    [Fact]
    public void CreatePrivateDirectory_makes_the_root_owner_only()
    {
        if (!OperatingSystem.IsLinux()) return;

        var created = AgentIsolation.CreatePrivateDirectory("ild-private-test", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(Directory.Exists(created));
            Assert.StartsWith(
                Path.TrimEndingDirectorySeparator(AgentIsolation.PrivateRoot),
                created,
                StringComparison.Ordinal);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(AgentIsolation.PrivateRoot));
        }
        finally
        {
            Directory.Delete(created, recursive: true);
        }
    }

    [Fact]
    public void ScratchRoot_is_the_configured_value_or_tmpdir()
    {
        Assert.Equal("/configured/scratch", AgentIsolation.ResolveScratchRoot("/configured/scratch"));
        Assert.Equal(Path.GetTempPath(), AgentIsolation.ResolveScratchRoot(null));
        Assert.Equal(Path.GetTempPath(), AgentIsolation.ResolveScratchRoot("   "));
    }

    [Fact]
    public void StripOrchestratorEnvironment_removes_every_secret()
    {
        var psi = new ProcessStartInfo("/bin/sh");
        foreach (var key in AgentIsolation.SecretEnvironmentKeys)
            psi.Environment[key] = "inherited-" + key;

        AgentIsolation.StripOrchestratorEnvironment(psi);

        Assert.Equal(Array.Empty<string>(), AgentIsolation.SecretEnvironmentKeys.Where(psi.Environment.ContainsKey).ToArray());
    }

    [Fact]
    public void StripOrchestratorEnvironment_covers_the_ten_orchestrator_secrets()
    {
        // Pinned by name: a new orchestrator secret added to the app without being
        // added here is the regression this guards.
        Assert.Equal(new[]
        {
            "ILD_AGENT_TOKEN",
            "ILD_API_TOKEN",
            "ILD_DB_CONNECTION_STRING",
            "ILD_PASSWORD",
            "ILD_SECRET_KEY",
            "ILD_SESSION_TOKEN_PEPPER",
            "ILD_USERNAME",
            "WORKITEM_API_KEYS",
            "WORKITEM_DB_CONNECTION_STRING",
            "ILD_WORKITEM_SERVER_API_KEY",
        }.OrderBy(k => k, StringComparer.Ordinal),
        AgentIsolation.SecretEnvironmentKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void StripOrchestratorEnvironment_leaves_everything_else_alone()
    {
        // The scrub is by exact name, not by pattern: a preview's own secrets and
        // the git commit identity travel on the same environment under different
        // names and must survive.
        var psi = new ProcessStartInfo("/bin/sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add("npm run dev");
        psi.Environment["ILD_PI_PROVIDER_API_KEY"] = "agents-own-key";
        psi.Environment["GIT_AUTHOR_NAME"] = "ILD";
        psi.Environment["ILD_DATA_PATH"] = "/data";

        AgentIsolation.StripOrchestratorEnvironment(psi);

        Assert.Equal("agents-own-key", psi.Environment["ILD_PI_PROVIDER_API_KEY"]);
        Assert.Equal("ILD", psi.Environment["GIT_AUTHOR_NAME"]);
        Assert.Equal("/data", psi.Environment["ILD_DATA_PATH"]);
        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-lc", "npm run dev" }, psi.ArgumentList);
    }
}
