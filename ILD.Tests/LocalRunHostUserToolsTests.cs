using System.Diagnostics;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// The environment the local host gives a command, as a run container gives one when it is made: a home, an npm prefix in
/// it and its bin first on PATH. The callers leave these to the host, and a caller's PATH goes in front of the run's.
/// Runs alone because it sets HOME and PATH, and the orchestrator's secrets and roots, in the real process environment,
/// which is what a command is launched from.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class LocalRunHostUserToolsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "ild-user-tools-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

    public LocalRunHostUserToolsTests()
    {
        Set("HOME", _home);
        Set("PATH", string.Join(Path.PathSeparator, "/usr/bin", "/bin"));
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private void Set(string name, string? value)
    {
        _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private string Bin => Path.Combine(_home, ".local", "bin");

    private static ProcessStartInfo Launch(IReadOnlyDictionary<string, string?>? environment = null, bool agent = false)
    {
        var psi = new ProcessStartInfo("/bin/sh");
        return LocalRunHost.Launch(agent
            ? RunCommand.Agent(psi, Guid.NewGuid(), null, NetworkMode.Off, environment)
            : RunCommand.Prepared(psi, environment: environment), EgressProxyTests.Creds);
    }

    [Fact]
    public void A_command_gets_an_npm_prefix_in_its_home_and_its_bin_first_on_path()
    {
        var psi = Launch();

        Assert.Equal(_home, psi.Environment["HOME"]);
        Assert.Equal(Path.Combine(_home, ".local"), psi.Environment["NPM_CONFIG_PREFIX"]);
        Assert.Equal(string.Join(Path.PathSeparator, Bin, "/usr/bin", "/bin"), psi.Environment["PATH"]);
        Assert.True(Directory.Exists(Bin));
    }

    [Fact]
    public void A_bin_already_last_on_ilds_path_is_moved_first_not_added_twice()
    {
        Set("PATH", string.Join(Path.PathSeparator, "/usr/bin", Bin));

        var psi = Launch();

        Assert.Equal(string.Join(Path.PathSeparator, Bin, "/usr/bin"), psi.Environment["PATH"]);
    }

    [Fact]
    public void A_command_with_no_path_gets_just_the_bin()
    {
        Set("PATH", null);

        var psi = Launch();

        Assert.Equal(Bin, psi.Environment["PATH"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_callers_path_goes_in_front_of_the_runs_and_its_home_and_npm_prefix_are_the_hosts(bool agent)
    {
        var psi = Launch(new Dictionary<string, string?>
        {
            ["PATH"] = string.Join(Path.PathSeparator, "/caller/bin", "/usr/bin"),
            ["HOME"] = "/caller/home",
            ["NPM_CONFIG_PREFIX"] = "/caller/prefix",
            ["FROM_CALLER"] = "yes",
        }, agent);

        // The caller's /usr/bin is the run's already, so it stays where the run has it.
        Assert.Equal(string.Join(Path.PathSeparator, "/caller/bin", Bin, "/usr/bin", "/bin"), psi.Environment["PATH"]);
        Assert.Equal(_home, psi.Environment["HOME"]);
        Assert.Equal(Path.Combine(_home, ".local"), psi.Environment["NPM_CONFIG_PREFIX"]);
        Assert.Equal("yes", psi.Environment["FROM_CALLER"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_launch_has_none_of_the_orchestrators_secrets_or_roots_but_keeps_what_its_caller_set(bool agent)
    {
        var roots = new[] { AgentIsolation.ScratchRootEnvVar, AgentIsolation.PrivateRootEnvVar, AgentIsolation.AgentReadRootEnvVar };
        var seeded = AgentIsolation.SecretEnvironmentKeys.Concat(roots).ToList();
        foreach (var name in seeded)
            Set(name, "orchestrator-only");
        Set("ILD_TEST_INHERITED", "kept");

        var psi = Launch(new Dictionary<string, string?> { ["ILD_API_TOKEN"] = "set-on-purpose", ["ILD_TEST_INHERITED"] = null }, agent);

        Assert.Equal("set-on-purpose", psi.Environment["ILD_API_TOKEN"]);
        var leaked = seeded.Where(name => name != "ILD_API_TOKEN" && psi.Environment.ContainsKey(name)).ToList();
        Assert.True(leaked.Count == 0, "leaked to the launch: " + string.Join(", ", leaked));
        Assert.False(psi.Environment.ContainsKey("ILD_TEST_INHERITED"));
        Assert.Contains("MSBUILDDISABLENODEREUSE", psi.Environment.Keys);
    }

    [Fact]
    public void The_start_infos_own_environment_is_not_the_launchs()
    {
        var psi = new ProcessStartInfo("/bin/sh");
        psi.Environment["ILD_ONLY_ON_THE_START_INFO"] = "x";

        var launched = LocalRunHost.Launch(RunCommand.Prepared(psi), EgressProxyTests.Creds);

        Assert.False(launched.Environment.ContainsKey("ILD_ONLY_ON_THE_START_INFO"));
    }

    [Fact]
    public void The_proxy_is_the_hosts_and_a_caller_cannot_change_it()
    {
        var psi = new ProcessStartInfo("/bin/sh");
        var command = RunCommand.Prepared(psi, environment: new Dictionary<string, string?> { ["HTTP_PROXY"] = "http://caller.invalid:9" }) with
        {
            EgressProxy = "http://127.0.0.1:3128",
        };

        var launched = LocalRunHost.Launch(command, EgressProxyTests.Creds);

        Assert.Equal("http://127.0.0.1:3128", launched.Environment["HTTP_PROXY"]);
        Assert.Equal("1", launched.Environment["NODE_USE_ENV_PROXY"]);
    }
}
