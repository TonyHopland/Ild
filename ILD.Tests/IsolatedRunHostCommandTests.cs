using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>
/// What a command started in a run is given, whichever engine holds the run: its environment and what never reaches it, the
/// egress proxy at ILD's host, the files a launch declares, and how it and what it left running are killed.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class IsolatedRunHostCommandTests : IDisposable
{
    private readonly string? _previousProxyPort = Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar);
    private readonly Guid _run = Guid.NewGuid();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"ild-isolated-{Guid.NewGuid():N}");
    private readonly InMemoryRunHost _host = NewHost();

    public IsolatedRunHostCommandTests()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, "3128");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, _previousProxyPort);
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    private static InMemoryRunHost NewHost(string? agentReadRoot = null)
        => agentReadRoot is null
            ? new InMemoryRunHost { Credentials = EgressProxyTests.Creds }
            : new InMemoryRunHost { Credentials = EgressProxyTests.Creds, AgentReadRoot = () => agentReadRoot };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string WorkingDirectory => RunPaths.WorkingDirectory(_run);

    private static ProcessStartInfo Command(string workingDirectory, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private async Task RunAsync(InMemoryRunHost host, RunCommand command)
    {
        using var process = await host.StartCommandAsync(command, Ct);
        await process.WaitForExitAsync(Ct);
    }

    // -- Environment -------------------------------------------------------------

    [Fact]
    public async Task A_callers_path_cannot_hide_the_runs_toolchains_and_its_home_is_the_runs()
    {
        var psi = Command(WorkingDirectory, "npm", "test");

        await RunAsync(_host, RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["PATH"] = "/caller/bin", ["HOME"] = "/caller/home" }));

        var exec = _host.Execs.Last();
        Assert.Equal($"HOME={RunPaths.HomeDirectory(_run)}", exec.Env.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.DoesNotContain(exec.Env, c => c.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal("/caller/bin", exec.Command[7]);
    }

    [Fact]
    public async Task A_variable_the_caller_set_reaches_the_run_and_one_only_ilds_own_environment_has_does_not()
    {
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "8080");
        // Set before the start info is made: it copies ILD's environment when it is created.
        Environment.SetEnvironmentVariable("INHERITED_ONLY_FROM_ILD", "8080");
        try
        {
            var psi = Command(WorkingDirectory, "dotnet", "run");

            await RunAsync(_host, RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["ASPNETCORE_HTTP_PORTS"] = "8080" }));

            var launch = _host.Execs.Last().Env;
            Assert.Contains("ASPNETCORE_HTTP_PORTS=8080", launch);
            Assert.DoesNotContain("INHERITED_ONLY_FROM_ILD=8080", launch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", previous);
            Environment.SetEnvironmentVariable("INHERITED_ONLY_FROM_ILD", null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task None_of_ilds_secrets_or_inherited_variables_reach_a_command_in_the_run(bool asAgent)
    {
        const string extra = "ILD_TEST_EXTRA_SECRET";
        var previousDenylist = Environment.GetEnvironmentVariable(AgentIsolation.SecretEnvDenylistEnvVar);
        Environment.SetEnvironmentVariable(AgentIsolation.SecretEnvDenylistEnvVar, extra);
        Environment.SetEnvironmentVariable("ILD_INHERITED_PROBE", "inherited-probe");
        try
        {
            var psi = Command(WorkingDirectory, "/bin/sh", "-c", "true");
            var secrets = AgentIsolation.SecretEnvironmentKeys;
            Assert.Contains(extra, secrets);
            foreach (var name in secrets) psi.Environment[name] = "secret-" + name;
            var caller = new Dictionary<string, string?> { ["ILD_CALLER_SET"] = "kept" };

            await RunAsync(_host, asAgent
                ? RunCommand.Agent(psi, _run, null, NetworkMode.Whitelist, caller)
                : RunCommand.Prepared(psi, _run, caller));

            var exec = _host.Execs.Last();
            var launch = exec.Env;
            Assert.Contains("ILD_CALLER_SET=kept", launch);
            foreach (var name in secrets)
            {
                Assert.DoesNotContain(launch, c => c.StartsWith(name + "=", StringComparison.Ordinal) || c.Contains("secret-" + name, StringComparison.Ordinal));
                Assert.DoesNotContain(exec.Command, c => c.Contains("secret-" + name, StringComparison.Ordinal));
            }
            Assert.DoesNotContain(launch, c => c.StartsWith("ILD_INHERITED_PROBE=", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentIsolation.SecretEnvDenylistEnvVar, previousDenylist);
            Environment.SetEnvironmentVariable("ILD_INHERITED_PROBE", null);
        }
    }

    // -- The proxy and ILD -------------------------------------------------------

    [Fact]
    public async Task A_launch_reaches_ild_directly_so_its_host_is_in_NO_PROXY_and_the_gateway_url_uses_it()
    {
        await RunAsync(_host, RunCommand.Prepared(Command(WorkingDirectory, "curl"), _run));

        var launch = _host.Execs.Last().Env;
        foreach (var name in new[] { "NO_PROXY", "no_proxy" })
            Assert.Contains(launch, c => c.StartsWith(name + "=", StringComparison.Ordinal) && c.Split('=', 2)[1].Split(',').Contains("ild.example"));
        Assert.Equal("http://ild.example:8080", _host.GatewayApiUrl("http://elsewhere.invalid:1", RunPaths.WorkingDirectory(_run)));
    }

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public async Task An_agent_launch_in_a_proxied_mode_gets_the_proxy_at_ilds_host_in_the_run(NetworkMode mode)
    {
        var providerId = Guid.NewGuid();

        await RunAsync(_host, RunCommand.Agent(Command(WorkingDirectory, "claude", "-p", "hi"), _run, providerId, mode, null));

        var launch = _host.Execs.Last().Env;
        Assert.Contains(launch, e => e.StartsWith("HTTPS_PROXY=http://ild:", StringComparison.Ordinal) && e.EndsWith("@ild.example:3128", StringComparison.Ordinal));
        Assert.Equal(new LaunchScope(LaunchKind.Run, _run, providerId), AgentIsolationEgressTests.ScopeInEnvironment(launch, EgressProxyTests.Creds));
    }

    // -- Files a launch declares ---------------------------------------------------

    // A host whose agent read root is <paramref name="readRoot"/>, which notes what is written under the run's copy of it.
    private static InMemoryRunHost AgentReadHost(string readRoot, Dictionary<string, string> written)
    {
        var host = NewHost(readRoot);
        host.OnExec = async e =>
        {
            var input = e.WantsStdin ? Encoding.UTF8.GetString(await e.ReadStdinToEndAsync()) : null;
            if (input is not null && e.Command.Count > 7 && e.Command[6] == "sh" && e.Command[7].StartsWith(RunPaths.AgentReadDirectory, StringComparison.Ordinal))
                lock (written) written[e.Command[7]] = input;
            await e.ExitAsync(0);
        };
        return host;
    }

    private (string ReadRoot, string Npmrc) WriteNpmrc(string kind)
    {
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-package-feeds"));
        var npmrc = Path.Combine(readRoot, "ild-package-feeds", $"{_run:N}-{kind}-1.npmrc");
        File.WriteAllText(npmrc, "//feed/:_password=abc\n");
        return (readRoot, npmrc);
    }

    [Fact]
    public async Task Files_an_agent_launch_declares_are_copied_into_the_run_and_the_launch_is_pointed_at_the_copies()
    {
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-mcp-config"));
        var config = Path.Combine(readRoot, "ild-mcp-config", $"claude-{_run:N}-1.json");
        File.WriteAllText(config, "{\"mcp\":true}");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var psi = Command(WorkingDirectory, "claude", "--mcp-config", config);

        await RunAsync(host, RunCommand.Agent(psi, _run, null, NetworkMode.Off, new Dictionary<string, string?> { ["CFG"] = "--c=" + config }, [config]));

        var staged = $"{RunPaths.AgentReadDirectory}/ild-mcp-config/claude-{_run:N}-1.json";
        Assert.Equal("{\"mcp\":true}", written[staged]);
        var exec = host.Execs.Last();
        Assert.Contains(staged, exec.Command);
        Assert.Contains("CFG=--c=" + staged, exec.Env);
        Assert.DoesNotContain(exec.Env.Concat(exec.Command), c => c.Contains(readRoot, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Paths_in_a_prompt_or_declared_outside_the_launchs_own_files_are_not_copied_into_the_run()
    {
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-mcp-config"));
        var others = Path.Combine(readRoot, "ild-mcp-config", $"claude-{Guid.NewGuid():N}-1.json");
        File.WriteAllText(others, "other launch");
        File.WriteAllText(Path.Combine(_scratch, "auth.json"), "login");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var prompt = $"read {readRoot}/ then {others} and {readRoot}/../auth.json";
        var psi = Command(WorkingDirectory, "claude", "-p", prompt);

        await RunAsync(host, RunCommand.Agent(
            psi, _run, null, NetworkMode.Off,
            new Dictionary<string, string?> { ["CFG"] = $"{readRoot}/../auth.json" },
            [others, $"{readRoot}/ild-mcp-config/../../auth.json", Path.Combine(readRoot, "ild-mcp-config"), readRoot]));

        Assert.Empty(written);
        var exec = host.Execs.Last();
        Assert.Contains(prompt, exec.Command);
        Assert.Contains($"CFG={readRoot}/../auth.json", exec.Env);
    }

    [Fact]
    public async Task A_cmd_node_in_network_mode_off_gets_the_proxy_and_its_npm_config_inside_the_run()
    {
        var (readRoot, npmrc) = WriteNpmrc("cmd");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var psi = Command(WorkingDirectory, "/bin/sh", "-c", "npm install");

        await RunAsync(host, RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["NPM_CONFIG_USERCONFIG"] = npmrc }, [npmrc]));

        var staged = $"{RunPaths.AgentReadDirectory}/ild-package-feeds/{_run:N}-cmd-1.npmrc";
        Assert.Equal("//feed/:_password=abc\n", written[staged]);
        var launch = host.Execs.Last().Env;
        Assert.Contains($"NPM_CONFIG_USERCONFIG={staged}", launch);
        Assert.Contains(launch, c => c.StartsWith("HTTPS_PROXY=http://", StringComparison.Ordinal) && c.Contains("ild.example:3128", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_command_with_no_owner_gets_the_proxy_and_its_npm_config_inside_the_run()
    {
        var (readRoot, npmrc) = WriteNpmrc("preview");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var psi = Command(WorkingDirectory, "/bin/sh", "-c", "npm start");

        await RunAsync(host, RunCommand.Prepared(psi, environment: new Dictionary<string, string?> { ["NPM_CONFIG_USERCONFIG"] = npmrc }, files: [npmrc]));

        var staged = $"{RunPaths.AgentReadDirectory}/ild-package-feeds/{_run:N}-preview-1.npmrc";
        Assert.True(written.ContainsKey(staged));
        var launch = host.Execs.Last().Env;
        Assert.Contains($"NPM_CONFIG_USERCONFIG={staged}", launch);
        Assert.Contains(launch, c => c.StartsWith("HTTPS_PROXY=http://", StringComparison.Ordinal) && c.Contains("ild.example:3128", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_copilot_launch_reaches_the_listener_where_the_run_reaches_ild_and_trusts_the_staged_certificate()
    {
        var readRoot = Path.Combine(_scratch, "agent-read");
        var ca = Path.Combine(_scratch, "gateway-tls.pem");
        File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);

        await CopilotLaunch.RunAsync(host, _run, WorkingDirectory, readRoot, ca, "http://ild.example:8080");

        var launch = host.Execs.Last().Env;
        Assert.Contains("GH_HOST=ild.example:8442", launch);
        var staged = Assert.Single(launch, c => c.StartsWith("NODE_EXTRA_CA_CERTS=", StringComparison.Ordinal))["NODE_EXTRA_CA_CERTS=".Length..];
        Assert.StartsWith(RunPaths.AgentReadDirectory + "/", staged);
        Assert.Equal("-----BEGIN CERTIFICATE-----", written[staged]);
    }

    // -- Files ---------------------------------------------------------------------

    [Fact]
    public async Task Files_are_read_and_written_by_commands_in_the_run()
    {
        _host.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Joined.Contains("cat --")) await e.WriteStdoutAsync("{\"a\":1}");
            await e.ExitAsync(0);
        };
        var path = $"{WorkingDirectory}/ild.config.json";

        await using (var read = await _host.OpenReadAsync(path, Ct))
            Assert.Equal("{\"a\":1}", await new StreamReader(read!).ReadToEndAsync(Ct));
        await _host.WriteFileAsync(path, "new", Ct);

        var write = _host.Execs.Last();
        Assert.Equal("new", Encoding.UTF8.GetString(await write.ReadStdinToEndAsync()));
        Assert.Equal(path, write.Command[^2]);
    }

    // -- Killing -------------------------------------------------------------------

    [Fact]
    public async Task Kill_all_ends_what_still_runs_in_the_run_and_waits_for_it()
    {
        _host.OnExec = async e =>
        {
            await e.WriteStdoutAsync("2\n");
            await e.ExitAsync(0);
        };

        var killed = await _host.KillAllAsync(_run, Ct);

        Assert.Equal(2, killed);
        var kill = _host.Execs.Last();
        Assert.Equal(_host.NameOf(_run), kill.Name);
        Assert.Equal(ExecRunHost.KillAllScript, kill.Command[5]);
    }

    [Fact]
    public async Task Killing_one_command_kills_its_process_group_by_the_name_it_was_started_under()
    {
        var hold = new TaskCompletionSource();
        _host.OnExec = async e =>
        {
            if (e.Joined.Contains("long-job")) await hold.Task;
            else await e.ExitAsync(0);
        };
        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "long-job"), _run), Ct);
        var startedAs = Assert.Single(_host.Execs, e => e.Joined.Contains("long-job")).Command[5];

        var killedOk = await Task.Run(() => process.TryKill(), Ct);

        Assert.True(killedOk);
        var kill = _host.Execs.Last();
        Assert.Equal(startedAs, kill.Command[^1]);
        Assert.Contains("kill -s TERM", kill.Command[5]);
        hold.SetResult();
    }
}
