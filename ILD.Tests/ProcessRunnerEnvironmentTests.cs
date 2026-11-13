using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// What <see cref="ProcessRunner"/> hands each kind of child: an orchestrator child
/// never inherits the orchestrator's secrets by accident, and an agent-scoped child
/// gets neither those nor the orchestrator's git credentials.
/// </summary>
public class ProcessRunnerEnvironmentTests
{
    private static Dictionary<string, string> Inherited()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = "/home/orchestrator",
            ["GIT_AUTHOR_NAME"] = "Seed Author",
            ["GIT_COMMITTER_EMAIL"] = "committer@example.test",
            ["GIT_ASKPASS"] = "/orchestrator/askpass.sh",
            ["ILD_GIT_USERNAME"] = "orchestrator-user",
            ["ILD_GIT_PASSWORD"] = "orchestrator-password",
        };
        foreach (var key in AgentIsolation.SecretEnvironmentKeys) env[key] = "secret-" + key;
        return env;
    }

    private static Dictionary<string, string> Env(System.Diagnostics.ProcessStartInfo psi)
        => psi.Environment.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);

    [Fact]
    public void An_orchestrator_child_does_not_inherit_the_orchestrators_secrets()
    {
        var psi = ProcessRunner.BuildStartInfo("npm", ["install"], "/work", null, null, Inherited());

        var env = Env(psi);
        Assert.All(AgentIsolation.SecretEnvironmentKeys, key => Assert.DoesNotContain(key, env.Keys));
        Assert.Equal("/usr/bin:/bin", env["PATH"]);
        Assert.Equal("Seed Author", env["GIT_AUTHOR_NAME"]);
    }

    [Fact]
    public void A_value_the_caller_passes_explicitly_still_reaches_the_child()
    {
        var key = AgentIsolation.SecretEnvironmentKeys.First();
        var psi = ProcessRunner.BuildStartInfo(
            "npm", ["install"], "/work", new Dictionary<string, string?> { [key] = "explicit" }, null, Inherited());

        Assert.Equal("explicit", Env(psi)[key]);
    }

    [Fact]
    public void A_clean_environment_holds_only_what_the_caller_passes()
    {
        var psi = ProcessRunner.BuildStartInfo(
            "git", ["fetch"], "/mirror",
            new Dictionary<string, string?> { ["PATH"] = "/usr/bin", ["GIT_TERMINAL_PROMPT"] = "0" },
            new ProcessRunOptions(CleanEnvironment: true), Inherited());

        Assert.Equal(new[] { "GIT_TERMINAL_PROMPT", "PATH" }, Env(psi).Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void An_agent_scoped_child_runs_as_built_without_the_orchestrators_secrets_or_git_credentials()
    {
        var psi = ProcessRunner.BuildStartInfo("git", ["status"], "/work", null, new ProcessRunOptions(AgentScoped: true), Inherited());

        Assert.Equal("git", psi.FileName);
        Assert.Equal(new[] { "status" }, psi.ArgumentList);

        var env = Env(psi);
        var leaked = AgentIsolation.SecretEnvironmentKeys
            .Concat(new[] { "GIT_ASKPASS", "ILD_GIT_USERNAME", "ILD_GIT_PASSWORD" })
            .Where(env.ContainsKey);
        Assert.Empty(leaked);
        Assert.Equal("Seed Author", env["GIT_AUTHOR_NAME"]);
        Assert.Equal("committer@example.test", env["GIT_COMMITTER_EMAIL"]);
        Assert.Equal("/home/orchestrator", env["HOME"]);
    }
}
