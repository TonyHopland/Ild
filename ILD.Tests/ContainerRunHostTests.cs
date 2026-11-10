using Moq;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Container;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Tools;
using ILD.Tests.Fakes;
using Porta.Pty;

namespace ILD.Tests;

[Collection(ProcessGlobalStateCollection.Name)]
public sealed class ContainerRunHostTests : IAsyncLifetime
{
    private const string Instance = "main";

    private readonly string? _previousProxyPort = Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar);
    private readonly Guid _run = Guid.NewGuid();
    private static readonly Guid Repo = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRepo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"ild-container-{Guid.NewGuid():N}");
    private readonly RecordingLogger<ContainerRunHost> _log = new();
    private FakeDockerApi _api = null!;
    private ContainerRunHost _host = null!;

    public ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, "3128");
        Directory.CreateDirectory(_scratch);
        _api = FakeDockerApi.Start();
        _host = NewHost(Options());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await _api.DisposeAsync();
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, _previousProxyPort);
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    private ContainerRunHostOptions Options(string instance = Instance, string? ildContainer = null)
        => ContainerRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => "http://ild.example:8080",
            "ILD_CONTAINER_SOCKET" => _api.SocketPath,
            "ILD_CONTAINER_RUNNER_IMAGE" => "registry.example/ild-runner:1",
            "ILD_CONTAINER_INSTANCE" => instance,
            "ILD_CONTAINER_ILD_CONTAINER" => ildContainer,
            "ILD_RUN_CPU_LIMIT" => "2",
            "ILD_RUN_MEMORY_LIMIT" => "512Mi",
            "ILD_NETWORK_PROXY_PORT" => "3128",
            "GIT_AUTHOR_NAME" => "Ada",
            _ => null,
        }) with { ReadyTimeout = TimeSpan.FromSeconds(10) };

    private ContainerRunHost NewHost(ContainerRunHostOptions options, IReadOnlyList<RunAgent>? agents = null, TimeProvider? time = null)
        => new(options, new ProcessRunner(), _log)
        {
            Time = time ?? TimeProvider.System,
            Credentials = EgressProxyTests.Creds,
            ReadyPollInterval = TimeSpan.FromMilliseconds(10),
            ExitStatusTimeout = TimeSpan.FromMilliseconds(400),
            Agents = () => agents ?? [],
            ChatToolsRoot = () => Path.Combine(_scratch, "chat-tools"),
        };

    private string WorkingDirectory => RunPaths.WorkingDirectory(_run);

    private static string Name(Guid run) => ContainerRunHost.ObjectName(run);

    private string MakeBaseRepository(string folder = "base", params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_scratch, folder);
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(path, "a.txt"), "a");
        foreach (var (file, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(path, file))!);
            File.WriteAllText(Path.Combine(path, file), content);
        }
        Git(path, "add", "-A");
        Git(path, "-c", "user.email=t@example.test", "-c", "user.name=T", "commit", "-q", "-m", "first");
        Git(path, "remote", "add", "origin", "https://user:secret@forge.example/acme/app.git");
        return path;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    private async Task<RunEnvironment> CreateRunAsync(ContainerRunHost? host = null)
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        return await (host ?? _host).CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);
    }

    private static System.Diagnostics.ProcessStartInfo Command(string workingDirectory, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private ContainerRunHost AgentReadHost(string readRoot, Dictionary<string, string> written)
    {
        var host = new ContainerRunHost(Options(), new ProcessRunner(), _log)
        {
            Credentials = EgressProxyTests.Creds,
            ReadyPollInterval = TimeSpan.FromMilliseconds(10),
            ExitStatusTimeout = TimeSpan.FromMilliseconds(400),
            Agents = () => [],
            ChatToolsRoot = () => Path.Combine(_scratch, "chat-tools"),
            AgentReadRoot = () => readRoot,
        };
        _api.OnExec = async e =>
        {
            if (e.WantsStdin)
            {
                var content = Encoding.UTF8.GetString(await e.ReadStdinToEndAsync());
                var target = e.Command.FirstOrDefault(c => c.StartsWith("/run/ild/agent-read", StringComparison.Ordinal));
                if (target is not null) written[target] = content;
            }
            await e.ExitAsync(0);
        };
        return host;
    }

    [Fact]
    public async Task Files_an_agent_launch_declares_are_copied_into_the_container_and_the_launch_is_pointed_at_the_copies()
    {
        await CreateRunAsync();
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-mcp-config"));
        var config = Path.Combine(readRoot, "ild-mcp-config", $"claude-{_run:N}-1.json");
        File.WriteAllText(config, "{\"mcp\":true}");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);

        using var process = await host.StartCommandAsync(
            RunCommand.Agent(Command(WorkingDirectory, "claude", "--mcp-config", config), _run, null, NetworkMode.Off, null, [config]),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var staged = $"/run/ild/agent-read/ild-mcp-config/claude-{_run:N}-1.json";
        Assert.Equal("{\"mcp\":true}", written[staged]);
        var launch = _api.Execs.Last().Command;
        Assert.Contains(staged, launch);
        Assert.DoesNotContain(launch, c => c.Contains(readRoot, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_copilot_launch_reaches_the_listener_where_the_container_reaches_ild_and_trusts_the_staged_certificate()
    {
        await CreateRunAsync();
        var readRoot = Path.Combine(_scratch, "agent-read");
        var ca = Path.Combine(_scratch, "gateway-tls.pem");
        File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);

        await CopilotLaunch.RunAsync(host, _run, WorkingDirectory, readRoot, ca, "http://ild.example:8080");

        var launch = _api.Execs.Last().Env;
        Assert.Contains("GH_HOST=ild.example:8442", launch);
        var staged = Assert.Single(launch, c => c.StartsWith("NODE_EXTRA_CA_CERTS=", StringComparison.Ordinal))["NODE_EXTRA_CA_CERTS=".Length..];
        Assert.StartsWith(RunPaths.AgentReadDirectory + "/", staged);
        Assert.Equal("-----BEGIN CERTIFICATE-----", written[staged]);
    }

    [Fact]
    public async Task Paths_in_a_prompt_are_not_copied_into_the_container()
    {
        await CreateRunAsync();
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-mcp-config"));
        var others = Path.Combine(readRoot, "ild-mcp-config", $"claude-{Guid.NewGuid():N}-1.json");
        File.WriteAllText(others, "other launch");
        File.WriteAllText(Path.Combine(_scratch, "auth.json"), "login");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var prompt = $"read {readRoot}/ then {others} and {readRoot}/../auth.json";

        using var process = await host.StartCommandAsync(
            RunCommand.Agent(Command(WorkingDirectory, "claude", "-p", prompt), _run, null, NetworkMode.Off, null),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(written);
        Assert.Contains(prompt, _api.Execs.Last().Command);
    }

    // -- Create ------------------------------------------------------------------

    [Fact]
    public async Task Create_makes_the_volume_then_the_network_then_the_container_each_with_the_run_labels()
    {
        var env = await CreateRunAsync();

        Assert.Equal(WorkingDirectory, env.WorkingDirectory);
        Assert.Equal([$"volumes/{Name(_run)}", $"networks/{Name(_run)}", $"containers/{Name(_run)}"], _api.Created);
        foreach (var labels in new[]
        {
            _api.Network(Name(_run))!["Labels"]!,
            _api.Container(Name(_run))!["Labels"]!,
        })
        {
            Assert.Equal("ild", labels["app.kubernetes.io/managed-by"]!.GetValue<string>());
            Assert.Equal(Instance, labels["ild/instance"]!.GetValue<string>());
            Assert.Equal(_run.ToString("N"), labels["ild/run"]!.GetValue<string>());
        }
        Assert.True(_api.Network(Name(_run))!["Internal"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Create_gives_the_container_no_privileges_the_chosen_limits_and_its_own_network_and_volume()
    {
        await CreateRunAsync();

        var container = _api.Container(Name(_run))!;
        var host = container["HostConfig"]!;
        Assert.Equal("registry.example/ild-runner:1", container["Image"]!.GetValue<string>());
        Assert.Equal("10001:10001", container["User"]!.GetValue<string>());
        Assert.Equal(["/usr/bin/tini", "--"], container["Entrypoint"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(["sleep", "infinity"], container["Cmd"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(RunPaths.RunsRoot, container["WorkingDir"]!.GetValue<string>());
        Assert.Equal(["ALL"], host["CapDrop"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(["no-new-privileges"], host["SecurityOpt"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(512L * 1024 * 1024, host["Memory"]!.GetValue<long>());
        Assert.Equal(2_000_000_000L, host["NanoCpus"]!.GetValue<long>());
        Assert.Equal(Name(_run), host["NetworkMode"]!.GetValue<string>());
        Assert.Equal([$"{Name(_run)}:/ild-run:rw"], host["Binds"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Contains("uid=10001", host["Tmpfs"]!["/run/ild"]!.GetValue<string>());
        var env = container["Env"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains($"HOME={RunPaths.HomeDirectory(_run)}", env);
        Assert.Contains("GIT_AUTHOR_NAME=Ada", env);
    }

    [Fact]
    public async Task Create_on_podman_hands_the_state_directory_to_the_runner_with_podmans_own_option()
    {
        _api.LibpodInfo = JsonNode.Parse("""{"host":{"security":{"rootless":true}}}""");

        await CreateRunAsync();

        var options = _api.Container(Name(_run))!["HostConfig"]!["Tmpfs"]!["/run/ild"]!.GetValue<string>().Split(',');
        Assert.Contains("U", options);
        Assert.DoesNotContain(options, o => o.StartsWith("uid=", StringComparison.Ordinal));
    }

    private async Task CreateOtherRunAsync(Guid run)
        => await _host.CreateAsync(new RunEnvironmentRequest(run, MakeBaseRepository($"base-{run:N}"), "ild/wi-2-run", Repo), TestContext.Current.CancellationToken);

    private string StateOptions(Guid run)
        => _api.Container(Name(run))!["HostConfig"]!["Tmpfs"]!["/run/ild"]!.GetValue<string>();

    [Theory]
    [InlineData(503)]
    [InlineData(403)]
    public async Task A_failed_podman_probe_is_not_remembered_as_docker(int status)
    {
        var other = Guid.NewGuid();
        _api.LibpodInfo = JsonNode.Parse("""{"host":{"security":{"rootless":true}}}""");
        _api.LibpodInfoStatus = status;

        // Podman refuses the Docker options this container is given while the probe fails.
        await Assert.ThrowsAnyAsync<Exception>(() => CreateRunAsync());
        _api.LibpodInfoStatus = null;
        await CreateOtherRunAsync(other);

        Assert.Contains("U", StateOptions(other).Split(','));
    }

    [Fact]
    public async Task A_failed_podman_probe_is_settled_by_the_engines_version_when_it_names_podman()
    {
        _api.LibpodInfo = JsonNode.Parse("""{"host":{"security":{"rootless":true}}}""");
        _api.LibpodInfoStatus = 403;
        _api.Version = JsonNode.Parse("""{"Components":[{"Name":"Podman Engine"}]}""");

        await CreateRunAsync();

        Assert.Contains("U", StateOptions(_run).Split(','));
    }

    [Fact]
    public async Task A_missing_podman_endpoint_is_asked_about_once()
    {
        await CreateRunAsync();
        await CreateOtherRunAsync(Guid.NewGuid());

        Assert.Equal(1, _api.Requests.Count(r => r.Contains("/libpod/info", StringComparison.Ordinal)));
        Assert.Contains("uid=10001", StateOptions(_run));
    }

    [Fact]
    public async Task Create_gives_the_container_no_working_nameserver_unless_the_run_asks_for_dns()
    {
        await CreateRunAsync();

        Assert.Equal(["127.0.0.1"], _api.Container(Name(_run))!["HostConfig"]!["Dns"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    [Fact]
    public async Task A_run_that_asks_for_dns_gets_the_engines_resolver()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo, Dns: true), TestContext.Current.CancellationToken);

        Assert.Empty(_api.Container(Name(_run))!["HostConfig"]!["Dns"]!.AsArray());
    }

    [Fact]
    public async Task A_new_environment_probes_for_a_direct_way_out_and_a_refused_connect_makes_the_rules_enforced()
    {
        var probe = new ILD.Core.Services.Implementations.Network.NetworkEnforcementProbe(_ => null, ildReaches: (_, _, _) => Task.FromResult(true));
        _host.Probe = probe;
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Command.Any(c => c.Contains("/dev/tcp/", StringComparison.Ordinal))) await e.WriteStdoutAsync("1\n");
            await e.ExitAsync(0);
        };
        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.EscapeObserved is null && DateTime.UtcNow < deadline) await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.False(probe.EscapeObserved);
    }

    [Fact]
    public async Task Create_names_ild_by_the_network_gateway_when_ild_runs_on_the_host()
    {
        await CreateRunAsync();

        var hosts = _api.Container(Name(_run))!["HostConfig"]!["ExtraHosts"]!.AsArray().Select(c => c!.GetValue<string>());
        Assert.Single(hosts, h => h == $"ild.example:{_api.SubnetOf(Name(_run))![..^"0/28".Length]}1");
    }

    [Fact]
    public async Task Create_connects_ilds_own_container_to_the_run_network_and_names_it_by_its_address_there()
    {
        _api.SeedIldContainer("ild-main");
        using var host = NewHost(Options(ildContainer: "ild-main"));

        await CreateRunAsync(host);

        Assert.Equal([Name(_run)], _api.NetworksOf("ild-main"));
        var hosts = _api.Container(Name(_run))!["HostConfig"]!["ExtraHosts"]!.AsArray().Select(c => c!.GetValue<string>());
        Assert.Single(hosts, h => h == $"ild.example:{_api.SubnetOf(Name(_run))![..^"0/28".Length]}5");
    }

    [Fact]
    public async Task Create_waits_for_the_container_to_run_before_putting_the_code_in()
    {
        _api.ContainerReadsBeforeRunning = 3;

        await CreateRunAsync();

        var firstExec = _api.Requests.FindIndex(r => r.EndsWith("/exec", StringComparison.Ordinal));
        Assert.True(_api.Requests.Take(firstExec).Count(r => r == $"GET /v1.41/containers/{Name(_run)}/json") >= 4);
    }

    [Fact]
    public async Task Create_puts_the_base_repository_in_as_a_bundle_with_the_remote_as_origin_and_checks_out_the_run_branch()
    {
        var stdin = new Dictionary<FakeDockerExec, byte[]>();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) stdin[e] = await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };

        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        var clone = Assert.Single(_api.Execs, e => e.Joined.Contains("git clone"));
        Assert.Equal(WorkingDirectory, clone.Command[^2]);
        Assert.Equal("https://forge.example/acme/app.git", clone.Command[^1]);
        Assert.StartsWith("# v2 git bundle", Encoding.ASCII.GetString(stdin[clone], 0, 15));
        var checkout = Assert.Single(_api.Execs, e => e.Joined.Contains("git checkout"));
        Assert.Equal(["ild/wi-1-run"], checkout.Command.TakeLast(1));
    }

    [Fact]
    public async Task Create_takes_a_container_that_is_already_running_as_it_is()
    {
        await CreateRunAsync();
        var created = _api.Created.ToList();
        var execs = _api.Execs.Count;

        var env = await _host.CreateAsync(new RunEnvironmentRequest(_run, Path.Combine(_scratch, "base"), "b", Repo), TestContext.Current.CancellationToken);

        Assert.Equal(WorkingDirectory, env.WorkingDirectory);
        Assert.Equal(created, _api.Created);
        Assert.Equal(execs, _api.Execs.Count);
    }

    [Fact]
    public async Task Create_that_fails_to_put_the_code_in_removes_everything_it_made_and_says_why()
    {
        _api.SeedIldContainer("ild-main");
        using var host = NewHost(Options(ildContainer: "ild-main"));
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.WriteStderrAsync("fatal: bad bundle\n");
            await e.ExitAsync(e.Joined.Contains("git clone") ? 128 : 0);
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b", Repo), TestContext.Current.CancellationToken));

        Assert.Contains("fatal: bad bundle", ex.Message);
        Assert.Equal(["ild-main"], _api.Containers());
        Assert.Empty(_api.Volumes());
        Assert.Empty(_api.Networks());
        Assert.Empty(_api.NetworksOf("ild-main"));
    }

    [Fact]
    public async Task Create_without_the_runner_image_on_the_machine_says_to_build_it_and_leaves_nothing()
    {
        _api.ImagePresent = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b", Repo), TestContext.Current.CancellationToken));

        Assert.Contains("registry.example/ild-runner:1", ex.Message);
        Assert.Contains("build or pull", ex.Message);
        Assert.Empty(_api.Volumes());
        Assert.Empty(_api.Networks());
    }

    // -- Forwards ----------------------------------------------------------------

    [Fact]
    public async Task A_command_starts_with_the_declared_forwards_answering_on_the_runs_loopback_through_the_proxy()
    {
        await CreateRunAsync();
        _host.Forwards = _ => Task.FromResult<IReadOnlyList<RunForward>>(
            [new(15432, "postgres", 5432), new(16379, "bad,host", 6379)]);

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var sync = _api.Execs.Single(e => e.Command.Contains(ExecRunHost.ForwardsScript));
        Assert.Equal(Name(_run), sync.Container);
        var address = ForwardAddresses.Allocate(["postgres"])["postgres"];
        Assert.Equal(["15432", "postgres", "5432", address], sync.Command.TakeLast(4));
        // The name is mapped to that address first, by root, which owns the hosts file.
        var hosts = _api.Execs.Single(e => e.Command.Contains(ExecRunHost.HostsScript));
        Assert.Equal("0", hosts.User);
        Assert.Equal([address, "postgres"], hosts.Command.TakeLast(2));
        Assert.True(_api.Execs.IndexOf(hosts) < _api.Execs.IndexOf(sync));
        // A host that could carry a socat option is not given a relay.
        Assert.DoesNotContain("bad,host", sync.Command);
        Assert.True(_api.Execs.IndexOf(sync) < _api.Execs.FindIndex(e => e.Command.Contains("make-it")));
    }

    [Fact]
    public async Task With_no_forwards_a_command_costs_no_extra_exec_until_one_was_given_then_its_relays_are_stopped()
    {
        await CreateRunAsync();
        IReadOnlyList<RunForward> forwards = [];
        _host.Forwards = _ => Task.FromResult(forwards);
        var ct = TestContext.Current.CancellationToken;

        using (var first = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "a"), _run), ct)) await first.WaitForExitAsync(ct);
        Assert.DoesNotContain(_api.Execs, e => e.Command.Contains(ExecRunHost.ForwardsScript));

        forwards = [new(15432, "postgres", 5432)];
        using (var second = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "b"), _run), ct)) await second.WaitForExitAsync(ct);
        forwards = [];
        using (var third = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "c"), _run), ct)) await third.WaitForExitAsync(ct);

        var syncs = _api.Execs.Where(e => e.Command.Contains(ExecRunHost.ForwardsScript)).ToList();
        Assert.Equal(2, syncs.Count);
        Assert.Equal("5432", syncs[0].Command[^2]);
        // Only the proxy's host and port: every relay is stopped.
        Assert.Equal(["/bin/bash", "-c", ExecRunHost.ForwardsScript, "bash"], syncs[1].Command.Take(4));
        Assert.Equal(6, syncs[1].Command.Count);
    }

    // -- Exec --------------------------------------------------------------------

    [Fact]
    public async Task A_command_streams_its_output_and_reports_its_exit_code_with_only_the_environment_the_caller_set()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("hello\n");
            await e.WriteStderrAsync("warn\n");
            await e.ExitAsync(3);
        };
        var psi = Command(WorkingDirectory, "make-it", "a b");

        using var process = await _host.StartCommandAsync(
            RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["ILD_TEST_ONLY"] = "yes" }), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hello\n", await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("warn\n", await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, process.ExitCode);
        var exec = _api.Execs.Last();
        Assert.Equal(Name(_run), exec.Container);
        Assert.Contains("ILD_TEST_ONLY=yes", exec.Env);
        Assert.Equal($"HOME={RunPaths.HomeDirectory(_run)}", exec.Env.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.DoesNotContain(exec.Env, c => c.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.DoesNotContain(exec.Command, c => c.Contains("ILD_TEST_ONLY", StringComparison.Ordinal));
        Assert.Equal(["make-it", "a b"], exec.Command.TakeLast(2));
        Assert.Contains(WorkingDirectory, exec.Command);
    }

    [Fact]
    public async Task Closing_the_input_of_a_command_ends_what_it_reads_to_the_end()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            var input = Encoding.UTF8.GetString(await e.ReadStdinToEndAsync());
            await e.WriteStdoutAsync(input.ToUpperInvariant());
            await e.ExitAsync(0);
        };
        var psi = Command(WorkingDirectory, "cat");
        psi.RedirectStandardInput = true;

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run), TestContext.Current.CancellationToken);
        await process.StandardInput.WriteAsync("prompt text");
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("PROMPT TEXT", await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task A_dropped_stream_fails_the_command_instead_of_leaving_it_waiting()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("partial");
            await e.DropAsync();
        };

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "long-job"), _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal("partial", await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Contains("connection to the run's container", await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Proxy_variables_that_name_ilds_loopback_name_ilds_host_name_for_the_container()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "curl");

        using var process = await _host.StartCommandAsync(
            RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["HTTP_PROXY"] = "http://scope:abc@127.0.0.1:3128" }), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var proxy = Assert.Single(_api.Execs.Last().Env, c => c.StartsWith("HTTP_PROXY=", StringComparison.Ordinal));
        Assert.Contains("ild.example:3128", proxy);
        Assert.DoesNotContain("127.0.0.1", proxy);
    }

    [Fact]
    public async Task A_launch_reaches_ild_directly_so_its_host_is_in_NO_PROXY_and_the_gateway_url_uses_it()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "curl");
        
        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var launch = _api.Execs.Last().Env;
        foreach (var name in new[] { "NO_PROXY", "no_proxy" })
            Assert.Contains(launch, c => c.StartsWith(name + "=", StringComparison.Ordinal) && c.Split('=', 2)[1].Split(',').Contains("ild.example"));
        Assert.Equal("http://ild.example:8080", _host.GatewayApiUrl("http://elsewhere.invalid:1", RunPaths.WorkingDirectory(_run)));
    }

    [Fact]
    public async Task A_host_that_is_a_suffix_of_the_preview_domain_is_not_put_on_NO_PROXY_and_ild_is_reached_by_another_name()
    {
        _host = NewHost(Options() with { IldUrl = "http://ild:8080" });
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "curl");

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var noProxy = Assert.Single(_api.Execs.Last().Env, c => c.StartsWith("NO_PROXY=", StringComparison.Ordinal))[9..].Split(',');
        Assert.Contains("ild-api", noProxy);
        Assert.DoesNotContain(noProxy, e => "x.preview.ild".EndsWith(e.TrimStart('.'), StringComparison.Ordinal) && e is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"));
        Assert.Equal("http://ild-api:8080", _host.GatewayApiUrl(null, RunPaths.WorkingDirectory(_run)));
        var mcp = IldMcpServer.BuildEnvironment(null, environment: new RunFacingEnvironment(new TestProcessEnvironment { { "ILD_API_URL", "http://ild:8080" } }, _host, RunPaths.WorkingDirectory(_run)));
        Assert.Contains(new Uri((string)mcp["ILD_API_URL"]!).Host, noProxy);
        var hosts = _api.Container(Name(_run))!["HostConfig"]!["ExtraHosts"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains(hosts, h => h.StartsWith("ild-api:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Through_the_routing_host_a_launch_in_a_run_is_given_ild_by_the_name_on_its_no_proxy()
    {
        // As ILD is wired: the routing host in front. The proxy refuses ILD's own name, so a launch in the run given
        // compose's ild (the operator's setting) rather than ild-api has its model calls refused.
        _host = NewHost(Options() with { IldUrl = "http://ild:8080" });
        await CreateRunAsync();
        var routing = new RoutingRunHost(LocalRunHost.Shared, _host);
        var run = RunPaths.WorkingDirectory(_run);

        Assert.Equal("http://ild-api:8080", routing.GatewayApiUrl("http://ild:8080", run));
        var mcp = IldMcpServer.BuildEnvironment(null, environment: new RunFacingEnvironment(new TestProcessEnvironment { { "ILD_API_URL", "http://ild:8080" } }, routing, run));
        Assert.Equal("ild-api", new Uri((string)mcp["ILD_API_URL"]!).Host);
        // ild-api resolves only in runs; a launch on ILD's own machine keeps the setting.
        Assert.Equal("http://ild:8080", routing.GatewayApiUrl("http://ild:8080", "/somewhere/on/ilds/disk"));
    }

    [Fact]
    public async Task A_command_for_a_container_that_is_gone_says_so_instead_of_hanging()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "ls"), _run), TestContext.Current.CancellationToken));

        Assert.Contains("could not run a command in the container", ex.Message);
    }

    // -- Every command gets the proxy and its declared files ----------------------

    private (string ReadRoot, string Npmrc) WriteNpmrc(string kind)
    {
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-package-feeds"));
        var npmrc = Path.Combine(readRoot, "ild-package-feeds", $"{_run:N}-{kind}-1.npmrc");
        File.WriteAllText(npmrc, "//feed/:_password=abc\n");
        return (readRoot, npmrc);
    }

    [Fact]
    public async Task A_cmd_node_in_network_mode_off_gets_the_proxy_and_its_npm_config_inside_the_run()
    {
        await CreateRunAsync();
        var (readRoot, npmrc) = WriteNpmrc("cmd");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var psi = Command(WorkingDirectory, "/bin/sh", "-c", "npm install");

        using var process = await host.StartCommandAsync(
            RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["NPM_CONFIG_USERCONFIG"] = npmrc }, [npmrc]),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var staged = $"{RunPaths.AgentReadDirectory}/ild-package-feeds/{_run:N}-cmd-1.npmrc";
        Assert.Equal("//feed/:_password=abc\n", written[staged]);
        var launch = _api.Execs.Last().Env;
        Assert.Contains($"NPM_CONFIG_USERCONFIG={staged}", launch);
        Assert.Contains(launch, c => c.StartsWith("HTTPS_PROXY=http://", StringComparison.Ordinal) && c.Contains("ild.example:3128", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_command_with_no_owner_gets_the_proxy_and_its_npm_config_inside_the_run()
    {
        await CreateRunAsync();
        var (readRoot, npmrc) = WriteNpmrc("preview");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var psi = Command(WorkingDirectory, "/bin/sh", "-c", "npm start");

        using var process = await host.StartCommandAsync(
            RunCommand.Prepared(psi, environment: new Dictionary<string, string?> { ["NPM_CONFIG_USERCONFIG"] = npmrc }, files: [npmrc]),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var staged = $"{RunPaths.AgentReadDirectory}/ild-package-feeds/{_run:N}-preview-1.npmrc";
        Assert.True(written.ContainsKey(staged));
        var launch = _api.Execs.Last().Env;
        Assert.Contains($"NPM_CONFIG_USERCONFIG={staged}", launch);
        Assert.Contains(launch, c => c.StartsWith("HTTPS_PROXY=http://", StringComparison.Ordinal) && c.Contains("ild.example:3128", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_variable_the_caller_set_reaches_the_run_and_one_only_ilds_own_environment_has_does_not()
    {
        await CreateRunAsync();
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS");
        Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "8080");
        // Set before the start info is made: it copies ILD's environment when it is created.
        Environment.SetEnvironmentVariable("INHERITED_ONLY_FROM_ILD", "8080");
        try
        {
            var psi = Command(WorkingDirectory, "dotnet", "run");

            using var process = await _host.StartCommandAsync(
                RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["ASPNETCORE_HTTP_PORTS"] = "8080" }),
                TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            var launch = _api.Execs.Last().Env;
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
    public async Task None_of_ilds_secrets_or_inherited_variables_reach_a_command_in_the_container(bool asAgent)
    {
        await CreateRunAsync();
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

            var command = asAgent
                ? RunCommand.Agent(psi, _run, null, NetworkMode.Whitelist, caller)
                : RunCommand.Prepared(psi, _run, caller);
            using var process = await _host.StartCommandAsync(command, TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            var exec = _api.Execs.Last();
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

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public async Task An_agent_launch_in_a_proxied_mode_gets_the_proxy_at_ilds_host_in_the_container(NetworkMode mode)
    {
        await CreateRunAsync();
        var providerId = Guid.NewGuid();

        using var process = await _host.StartCommandAsync(
            RunCommand.Agent(Command(WorkingDirectory, "claude", "-p", "hi"), _run, providerId, mode, null),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_api.Execs.Last().Env, e => e.StartsWith("HTTPS_PROXY=http://ild:", StringComparison.Ordinal) && e.EndsWith("@ild.example:3128", StringComparison.Ordinal));
        Assert.Equal(new LaunchScope(LaunchKind.Run, _run, providerId), AgentIsolationEgressTests.ScopeInEnvironment(_api.Execs.Last().Env, EgressProxyTests.Creds));
    }

    // -- The direct route (network mode Off) -------------------------------------

    private async Task<IRunProcess> StartAsync(NetworkMode mode, bool prepared = false)
    {
        var psi = Command(WorkingDirectory, "ssh", "-T", "git@github.com");
        var command = prepared ? RunCommand.Prepared(psi, _run, networkMode: mode) : RunCommand.Agent(psi, _run, Guid.NewGuid(), mode, null);
        var process = await _host.StartCommandAsync(command, TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process;
    }

    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NODE_USE_ENV_PROXY", "NO_PROXY", "no_proxy"];

    // The exec of a started command: the one that runs the wrapper, whose own environment is the exec's Env field.
    private FakeDockerExec LaunchExec() => _api.Execs.Last(e => e.Command.Any(c => c.Contains("setsid -w", StringComparison.Ordinal)));

    private IEnumerable<string> LaunchEnvironment() => LaunchExec().Env.Where(c => ProxyVariables.Any(v => c.StartsWith(v + "=", StringComparison.Ordinal)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_launch_in_off_connects_the_container_to_the_open_network_and_gets_no_proxy_variables(bool prepared)
    {
        await CreateRunAsync();
        Assert.DoesNotContain(_host.OpenNetworkName, _api.NetworksOf(Name(_run)));

        using var process = await StartAsync(NetworkMode.Off, prepared);

        Assert.Contains(_host.OpenNetworkName, _api.NetworksOf(Name(_run)));
        Assert.Empty(LaunchEnvironment());
        Assert.Contains(LaunchExec().Command, c => c.StartsWith($"{_run:N}/{ExecRunHost.OffPrefix}", StringComparison.Ordinal));
        var open = _api.Network(_host.OpenNetworkName)!;
        Assert.False(open["Internal"]!.GetValue<bool>());
        Assert.Equal("open", open["Labels"]![RunPaths.RoleLabel]!.GetValue<string>());
        Assert.Equal(Instance, open["Labels"]![RunPaths.InstanceLabel]!.GetValue<string>());
        Assert.Equal(RunPaths.ManagedByValue, open["Labels"]![RunPaths.ManagedByLabel]!.GetValue<string>());
    }

    [Fact]
    public async Task A_launch_in_off_gives_the_container_a_nameserver_that_works_while_the_route_is_open()
    {
        await CreateRunAsync();

        using var process = await StartAsync(NetworkMode.Off);

        var write = _api.Execs.Single(e => e.Joined.Contains("/etc/resolv.conf", StringComparison.Ordinal));
        Assert.Equal("0", write.User);
        // Docker's resolver is in every container on a network of its own.
        Assert.Equal("127.0.0.11", write.Command[^1]);
        Assert.Equal("127.0.0.1", _api.Container(Name(_run))!["HostConfig"]!["Dns"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task On_podman_the_nameserver_while_the_route_is_open_is_the_open_networks_gateway()
    {
        _api.LibpodInfo = new JsonObject();
        await CreateRunAsync();

        using var process = await StartAsync(NetworkMode.Off);

        var nameserver = _api.Execs.Single(e => e.Joined.Contains("/etc/resolv.conf", StringComparison.Ordinal)).Command[^1];
        Assert.Matches(@"^10\.89\.\d+\.1$", nameserver);
    }

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    [InlineData(NetworkMode.Blacklist)]
    public async Task Returning_to_a_proxied_mode_with_no_off_process_left_disconnects_the_container_and_restores_the_nameserver(NetworkMode mode)
    {
        await CreateRunAsync();
        using (await StartAsync(NetworkMode.Off)) { }
        Assert.Contains(_host.OpenNetworkName, _api.NetworksOf(Name(_run)));

        using var process = await StartAsync(mode);

        Assert.DoesNotContain(_host.OpenNetworkName, _api.NetworksOf(Name(_run)));
        var restore = _api.Execs.Last(e => e.Joined.Contains("/etc/resolv.conf", StringComparison.Ordinal));
        Assert.Equal("127.0.0.1", restore.Command[^1]);
        Assert.NotEmpty(LaunchEnvironment());
    }

    [Theory]
    [InlineData(false, "127.0.0.11")]
    [InlineData(true, @"^10\.213\.\d+\.1$")]
    public async Task A_run_with_its_own_dns_gets_the_engines_nameserver_back_when_the_route_closes(bool podman, string expected)
    {
        if (podman) _api.LibpodInfo = new JsonObject();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo, Dns: true), TestContext.Current.CancellationToken);
        using (await StartAsync(NetworkMode.Off)) { }
        var openGateway = _api.Execs.Single(e => e.Joined.Contains("/etc/resolv.conf", StringComparison.Ordinal)).Command[^1];

        using var process = await StartAsync(NetworkMode.Monitor);

        var close = _api.Execs.Last(e => e.Joined.Contains("/etc/resolv.conf", StringComparison.Ordinal));
        Assert.Equal("0", close.User);
        Assert.Matches(expected, close.Command[^1]);
        Assert.NotEqual("127.0.0.1", close.Command[^1]);
        if (podman) Assert.NotEqual(openGateway, close.Command[^1]);
    }

    [Fact]
    public async Task Two_launches_in_off_at_once_both_start_and_the_container_is_connected_once()
    {
        await CreateRunAsync();
        var ct = TestContext.Current.CancellationToken;

        var both = await Task.WhenAll(StartAsync(NetworkMode.Off), StartAsync(NetworkMode.Off));

        Assert.Equal(2, both.Length);
        Assert.Single(_api.Connects, c => c["Container"]!.GetValue<string>() == Name(_run));
        foreach (var p in both) p.Dispose();
    }

    [Fact]
    public async Task Returning_to_a_proxied_mode_with_an_off_process_still_running_keeps_the_route()
    {
        await CreateRunAsync();
        using (await StartAsync(NetworkMode.Off)) { }
        // The pid file of a process launched in Off, whose process group is alive.
        _api.OnExec = async e =>
        {
            if (e.Command.Count > 2 && e.Command[2] == ExecRunHost.OffRunningScript) await e.ExitAsync(1);
            else await e.ExitAsync(0);
        };

        using var process = await StartAsync(NetworkMode.Monitor);

        Assert.Contains(_host.OpenNetworkName, _api.NetworksOf(Name(_run)));
        Assert.NotEmpty(LaunchEnvironment());
    }

    [Fact]
    public async Task A_proxied_launch_in_a_run_that_never_had_the_route_asks_nothing_of_the_network()
    {
        await CreateRunAsync();
        var execs = _api.Execs.Count;

        using var process = await StartAsync(NetworkMode.Monitor);

        Assert.DoesNotContain(_host.OpenNetworkName, _api.Networks());
        Assert.DoesNotContain(_api.Execs.Skip(execs), e => e.Command.Count > 2 && e.Command[2] == ExecRunHost.OffRunningScript);
    }

    [Fact]
    public async Task The_runs_that_have_a_direct_route_are_the_containers_on_the_open_network()
    {
        await CreateRunAsync();
        Assert.Empty(await _host.DirectRoutesAsync(TestContext.Current.CancellationToken));

        using var process = await StartAsync(NetworkMode.Off);

        Assert.Equal([new DirectRoute(_run, Chat: false)], await _host.DirectRoutesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_sweep_removes_the_open_network_once_nothing_is_on_it_and_not_before()
    {
        await CreateRunAsync();
        using (await StartAsync(NetworkMode.Off)) { }
        var ct = TestContext.Current.CancellationToken;

        Assert.DoesNotContain(await _host.ListLeftoversAsync(new HashSet<Guid> { _run }, ct), l => l.Location == _host.OpenNetworkName);

        using (await StartAsync(NetworkMode.Monitor)) { }
        var leftover = Assert.Single(await _host.ListLeftoversAsync(new HashSet<Guid> { _run }, ct), l => l.Location == _host.OpenNetworkName);
        Assert.True(await _host.DeleteLeftoverAsync(leftover, ct));
        Assert.DoesNotContain(_host.OpenNetworkName, _api.Networks());
    }

    // -- Kill leftovers ----------------------------------------------------------

    [Fact]
    public async Task Kill_all_ends_what_still_runs_in_the_container_and_waits_for_it()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("2\n");
            await e.ExitAsync(0);
        };

        var killed = await _host.KillAllAsync(_run, TestContext.Current.CancellationToken);

        Assert.Equal(2, killed);
        var kill = _api.Execs.Last();
        Assert.Equal(Name(_run), kill.Container);
        Assert.Equal(ExecRunHost.KillAllScript, kill.Command[2]);
    }

    [Fact]
    public async Task Kill_all_for_a_run_with_no_container_asks_nothing_of_the_engine_and_kills_nothing()
    {
        var killed = await _host.KillAllAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(0, killed);
        Assert.Empty(_api.Execs);
    }

    [Fact]
    public async Task Killing_one_command_kills_its_process_group_by_the_name_it_was_started_under()
    {
        await CreateRunAsync();
        var hold = new TaskCompletionSource();
        _api.OnExec = async e =>
        {
            if (e.Joined.Contains("long-job")) await hold.Task;
            else await e.ExitAsync(0);
        };
        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "long-job"), _run), TestContext.Current.CancellationToken);
        var startedAs = Assert.Single(_api.Execs, e => e.Joined.Contains("long-job")).Command[5];

        var killedOk = await Task.Run(() => process.TryKill(), TestContext.Current.CancellationToken);

        Assert.True(killedOk);
        var kill = _api.Execs.Last();
        Assert.Equal(startedAs, kill.Command[^1]);
        Assert.Contains("kill -s TERM", kill.Command[2]);
        hold.SetResult();
    }

    // -- Terminal, files, preview ------------------------------------------------

    [Fact]
    public async Task A_terminal_is_a_tty_exec_whose_input_output_and_end_go_through_the_pty_interface()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("ready$ ");
            while (!e.StdinSoFar().Contains('x')) await Task.Delay(10);
            await e.WriteStdoutAsync("got x");
            await e.ExitAsync(5);
        };
        var exited = new TaskCompletionSource<int>();

        using var pty = await _host.OpenTerminalAsync(
            new PtyOptions { Cwd = WorkingDirectory, App = "/bin/bash", Cols = 120, Rows = 30, Name = "t" }, TestContext.Current.CancellationToken);
        pty.ProcessExited += (_, args) => exited.TrySetResult(args.ExitCode);
        await pty.WriterStream.WriteAsync("x"u8.ToArray(), TestContext.Current.CancellationToken);
        var read = await new StreamReader(pty.ReaderStream).ReadToEndAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ready$ got x", read);
        Assert.Equal(5, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var exec = _api.Execs.Last();
        Assert.True(exec.Tty);
        Assert.Contains("120x30", exec.Resizes);
    }

    private const string GatewayToken = "gateway-token-value-1";
    private const string ApiToken = "ild-api-token-value-2";

    private static Dictionary<string, string?> Secrets() => new() { ["ANTHROPIC_API_KEY"] = GatewayToken, ["ILD_API_TOKEN"] = ApiToken };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_secret_of_a_commands_environment_is_in_any_exec_command_it_travels_in_the_exec_create_env_field(bool asAgent)
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "claude", "-p", "hi");

        using var process = await _host.StartCommandAsync(
            asAgent ? RunCommand.Agent(psi, _run, null, NetworkMode.Monitor, Secrets()) : RunCommand.Prepared(psi, _run, Secrets()),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = LaunchExec();
        Assert.Contains($"ANTHROPIC_API_KEY={GatewayToken}", exec.Env);
        Assert.Contains($"ILD_API_TOKEN={ApiToken}", exec.Env);
        Assert.DoesNotContain(_api.Execs.SelectMany(e => e.Command), c => c.Contains(GatewayToken, StringComparison.Ordinal) || c.Contains(ApiToken, StringComparison.Ordinal));
        Assert.DoesNotContain(_api.Execs, e => e.StdinSoFar().Contains(GatewayToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_secret_of_a_terminals_environment_is_in_the_exec_command_or_typed_into_the_terminal()
    {
        await CreateRunAsync();

        using var pty = await _host.OpenTerminalAsync(
            new PtyOptions
            {
                Cwd = WorkingDirectory, App = "/bin/bash", Cols = 80, Rows = 24, Name = "t",
                Environment = new Dictionary<string, string> { ["ILD_API_TOKEN"] = ApiToken },
            }, TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        Assert.True(exec.Tty);
        Assert.Contains($"ILD_API_TOKEN={ApiToken}", exec.Env);
        Assert.Contains($"HOME={RunPaths.HomeDirectory(_run)}", exec.Env);
        Assert.DoesNotContain(exec.Command, c => c.Contains(ApiToken, StringComparison.Ordinal));
        Assert.DoesNotContain(ApiToken, exec.StdinSoFar());
    }

    [Fact]
    public async Task A_callers_path_cannot_hide_the_runs_toolchains_and_its_home_is_the_runs()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "npm", "test");

        using var process = await _host.StartCommandAsync(
            RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["PATH"] = "/caller/bin", ["HOME"] = "/caller/home" }),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = LaunchExec();
        Assert.Equal($"HOME={RunPaths.HomeDirectory(_run)}", exec.Env.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.DoesNotContain(exec.Env, c => c.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal("/caller/bin", exec.Command[7]);
    }

    [Fact]
    public async Task Files_are_read_and_written_by_commands_in_the_container()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Joined.Contains("cat --")) await e.WriteStdoutAsync("{\"a\":1}");
            await e.ExitAsync(0);
        };
        var path = $"{WorkingDirectory}/ild.config.json";

        await using (var read = await _host.OpenReadAsync(path, TestContext.Current.CancellationToken))
            Assert.Equal("{\"a\":1}", await new StreamReader(read!).ReadToEndAsync(TestContext.Current.CancellationToken));
        await _host.WriteFileAsync(path, "new", TestContext.Current.CancellationToken);

        var write = _api.Execs.Last();
        Assert.Equal("new", Encoding.UTF8.GetString(await write.ReadStdinToEndAsync()));
        Assert.Equal(path, write.Command[^2]);
    }

    private async Task<int> WritesAfterOpeningForWrite(string existing, string? written)
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Joined.Contains("cat --")) await e.WriteStdoutAsync(existing);
            await e.ExitAsync(0);
        };
        var path = $"{WorkingDirectory}/scripts/build.sh";
        var before = _api.Execs.Count;
        await using (var stream = await _host.OpenFileForWriteAsync(path, TestContext.Current.CancellationToken))
        {
            if (written is not null)
            {
                stream.SetLength(0);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(written), TestContext.Current.CancellationToken);
            }
        }
        return _api.Execs.Skip(before).Count(e => e.Command.Contains(ExecRunHost.WriteFileScript));
    }

    [Fact]
    public async Task A_file_opened_for_write_but_not_written_is_not_rewritten()
        => Assert.Equal(0, await WritesAfterOpeningForWrite("abc", null));

    [Fact]
    public async Task A_file_saved_with_the_same_bytes_is_not_rewritten()
        => Assert.Equal(0, await WritesAfterOpeningForWrite("abc", "abc"));

    [Fact]
    public async Task An_edited_file_is_written_back_once_in_place()
    {
        Assert.Equal(1, await WritesAfterOpeningForWrite("abc", "changed"));
        var write = _api.Execs.Last();
        Assert.Equal("changed", Encoding.UTF8.GetString(await write.ReadStdinToEndAsync()));
        Assert.Equal("rw", write.Command[^1]);
    }

    [Fact]
    public async Task A_bundle_comes_out_of_the_container_into_the_sink()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("BUNDLE-BYTES");
            await e.ExitAsync(0);
        };
        using var sink = new MemoryStream();

        var result = await _host.PullBundleAsync(WorkingDirectory, "ild/wi-1-run", incremental: true, sink, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("BUNDLE-BYTES", Encoding.UTF8.GetString(sink.ToArray()));
        Assert.Contains("--remotes=origin", _api.Execs.Last().Command);
    }

    [Fact]
    public async Task The_preview_proxy_is_pointed_at_the_containers_address_on_its_network()
    {
        await CreateRunAsync();

        var target = await _host.PreviewTargetAsync(WorkingDirectory, 5173, TestContext.Current.CancellationToken);

        Assert.Matches(@"^http://10\.213\.\d+\.2:5173/$", target.ToString());
    }

    [Fact]
    public async Task The_preview_target_of_a_run_this_process_did_not_start_is_read_from_the_engine()
    {
        await CreateRunAsync();
        using var restarted = NewHost(Options());

        var target = await restarted.PreviewTargetAsync(WorkingDirectory, 3000, TestContext.Current.CancellationToken);

        Assert.Matches(@"^http://10\.213\.\d+\.2:3000/$", target.ToString());
    }

    [Fact]
    public void The_gateway_is_reached_at_the_name_ild_is_known_by()
        => Assert.Equal("http://ild.example:8080", _host.GatewayApiUrl(null, RunPaths.WorkingDirectory(_run)));

    // -- Delete and sweep --------------------------------------------------------

    [Fact]
    public async Task Delete_removes_the_container_then_the_volume_then_the_network()
    {
        var env = await CreateRunAsync();

        await _host.DeleteAsync(env, TestContext.Current.CancellationToken);

        Assert.Equal([$"containers/{Name(_run)}", $"volumes/{Name(_run)}", $"networks/{Name(_run)}"], _api.Deleted);
    }

    [Fact]
    public async Task Delete_takes_ild_off_the_run_network_so_the_network_can_go()
    {
        _api.SeedIldContainer("ild-main");
        using var host = NewHost(Options(ildContainer: "ild-main"));
        var env = await CreateRunAsync(host);

        await host.DeleteAsync(env, TestContext.Current.CancellationToken);

        Assert.Empty(_api.NetworksOf("ild-main"));
        Assert.Empty(_api.Networks());
    }

    [Fact]
    public async Task Delete_of_a_run_that_is_already_gone_is_not_an_error()
    {
        await _host.DeleteAsync(new RunEnvironment(Guid.NewGuid(), "/ild-run/x/repo"), TestContext.Current.CancellationToken);

        Assert.Empty(_api.Deleted);
    }

    private void SeedRun(Guid run, string instance, bool container = true, string managedBy = "ild")
    {
        _api.Seed("volumes", Name(run), instance, run, managedBy);
        _api.Seed("networks", Name(run), instance, run, managedBy);
        if (container) _api.Seed("containers", Name(run), instance, run, managedBy);
    }

    [Fact]
    public async Task The_sweeper_deletes_finished_and_missing_runs_and_keeps_live_ones_and_other_installs_objects()
    {
        var live = Guid.NewGuid();
        var finished = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var otherInstance = Guid.NewGuid();
        var notOurs = Guid.NewGuid();
        SeedRun(live, Instance);
        SeedRun(finished, Instance);
        SeedRun(orphan, Instance, container: false);
        SeedRun(otherInstance, "other");
        SeedRun(notOurs, Instance, managedBy: "someone-else");
        var sweeper = new RunHostSweeper(new RoutingRunHost(new FakeRunHost(), _host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { live }), TimeSpan.FromMinutes(1));

        await sweeper.SweepAsync(_host, TestContext.Current.CancellationToken);

        foreach (var kept in new[] { live, otherInstance, notOurs })
        {
            Assert.Contains(Name(kept), _api.Containers());
            Assert.Contains(Name(kept), _api.Volumes());
            Assert.Contains(Name(kept), _api.Networks());
        }
        foreach (var gone in new[] { finished, orphan })
        {
            Assert.DoesNotContain(Name(gone), _api.Containers());
            Assert.DoesNotContain(Name(gone), _api.Volumes());
            Assert.DoesNotContain(Name(gone), _api.Networks());
        }
    }

    [Fact]
    public async Task The_sweeper_keeps_the_runs_that_have_not_ended_even_when_the_caller_does_not_call_them_live()
    {
        var failed = Guid.NewGuid();
        SeedRun(failed, Instance);
        var retaining = new ContainerRunHost(Options(), new ProcessRunner(), _log) { RetainedRuns = _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { failed }) };
        var sweeper = new RunHostSweeper(new RoutingRunHost(new FakeRunHost(), retaining), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));

        await sweeper.SweepAsync(retaining, TestContext.Current.CancellationToken);
        retaining.Dispose();

        Assert.Contains(Name(failed), _api.Containers());
    }

    [Fact]
    public async Task A_killed_ild_leaves_nothing_behind_after_the_next_sweep()
    {
        _api.SeedIldContainer("ild-main");
        using var killed = NewHost(Options(ildContainer: "ild-main"));
        await CreateRunAsync(killed);
        Assert.NotEmpty(_api.Networks());

        // The process died with the run mid-way: a new one knows of no run, and finds the objects by their labels.
        using var restarted = NewHost(Options(ildContainer: "ild-main"));
        var sweeper = new RunHostSweeper(new RoutingRunHost(new FakeRunHost(), restarted), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));
        await sweeper.SweepAsync(restarted, TestContext.Current.CancellationToken);

        Assert.Equal(["ild-main"], _api.Containers());
        Assert.Empty(_api.Volumes());
        Assert.Empty(_api.Networks());
        Assert.Empty(_api.NetworksOf("ild-main"));
    }

    [Fact]
    public async Task A_sweep_while_the_engine_cannot_be_reached_lists_nothing_instead_of_failing_startup()
    {
        using var unreachable = NewHost(Options() with { Socket = Path.Combine(_scratch, "nothing.sock") });

        var leftovers = await unreachable.ListLeftoversAsync(new HashSet<Guid>(), TestContext.Current.CancellationToken);

        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task A_sweep_while_the_engine_errors_lists_nothing_and_says_so()
    {
        _api.Down = true;

        var leftovers = await _host.ListLeftoversAsync(new HashSet<Guid>(), TestContext.Current.CancellationToken);

        Assert.Empty(leftovers);
        Assert.Contains(_log.Warnings, w => w.Contains("Could not list"));
    }

    [Fact]
    public async Task A_command_with_no_working_directory_goes_to_the_run_it_is_for_and_an_unknown_owner_is_refused()
    {
        await CreateRunAsync();
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);

        using var inRun = await routing.StartCommandAsync(
            RunCommand.Prepared(Command("", "ls"), _run), TestContext.Current.CancellationToken);
        await inRun.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(local.Started);
        Assert.Equal(Name(_run), _api.Execs.Last().Container);

        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.StartCommandAsync(
            RunCommand.Prepared(Command("", "ls"), Guid.NewGuid()), TestContext.Current.CancellationToken));
        Assert.Empty(local.Started);
    }

    // -- Runs and chats from before containers: read and deleted, never run ------

    private static readonly string[] OldPaths = ["/worktrees/ild/old-run/repo", "/data/chat-sessions/11111111111111111111111111111111"];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task An_agent_launch_for_a_path_on_ilds_disk_starts_nothing_on_ilds_machine(int which)
    {
        await CreateRunAsync();
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var execs = _api.Execs.Count;
        var agent = RunCommand.Agent(Command(OldPaths[which], "claude"), _run, null, NetworkMode.Monitor, null) with { RunIn = RunPaths.HolderOf(OldPaths[which]) ?? _run };

        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.StartCommandAsync(agent, TestContext.Current.CancellationToken));

        Assert.Empty(local.Started);
        Assert.Equal(execs, _api.Execs.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_command_without_a_run_for_a_path_on_ilds_disk_starts_nothing_on_ilds_machine(int which)
    {
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);

        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.StartCommandAsync(
            RunCommand.Prepared(Command(OldPaths[which], "git"), Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Empty(local.Started);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_terminal_a_tool_and_a_bundle_for_a_path_on_ilds_disk_are_refused_and_reach_no_host(int which)
    {
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var path = OldPaths[which];
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.OpenTerminalAsync(new Porta.Pty.PtyOptions { Cwd = path, App = "sh" }, ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.RunToolAsync(path, "git", ["status"], null, ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.PushBundleAsync(path, "origin", new MemoryStream(), ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.FetchBundleAsync(path, new MemoryStream(), ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.PullBundleAsync(path, "main", false, new MemoryStream(), ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.AddChatToolsAsync(path, "/base", ct));
        Assert.Throws<PredatesContainersException>(() => routing.HoldChat(path));

        Assert.Empty(local.Started);
    }

    [Fact]
    public async Task Git_in_ilds_own_base_clone_still_reaches_the_local_host_but_in_an_old_worktree_it_does_not()
    {
        var baseClone = Path.Combine(_scratch, "base");
        Directory.CreateDirectory(Path.Combine(baseClone, ".git"));
        var routing = new RoutingRunHost(new FakeRunHost(), _host);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<NotSupportedException>(() => routing.RunToolAsync(baseClone, "git", ["branch", "-D", "x"], null, ct));
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.RunToolAsync(Path.Combine(_scratch, "not-a-repository"), "git", ["status"], null, ct));
    }

    [Fact]
    public async Task A_title_launch_in_a_chats_folder_under_a_run_id_of_its_own_still_goes_to_the_isolated_host()
    {
        var chat = Guid.NewGuid();
        var routing = new RoutingRunHost(new FakeRunHost(), _host);
        await routing.CreateChatFolderAsync(chat, "/unused", TestContext.Current.CancellationToken);
        var folder = ChatFolder(chat);

        using var process = await routing.StartCommandAsync(
            RunCommand.Agent(Command(folder, "claude"), Guid.NewGuid(), null, NetworkMode.Monitor, null) with { RunIn = RunPaths.HolderOf(folder) },
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ChatContainer, _api.Execs.Last().Container);
    }

    [Fact]
    public async Task Reading_and_deleting_on_ilds_disk_still_reach_the_local_host()
    {
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var ct = TestContext.Current.CancellationToken;

        var notes = OldPaths[0] + "/notes.md";
        await local.WriteFileAsync(notes, "history", ct);

        Assert.Equal("history", await routing.ReadTextFileAsync(notes, ct));
        Assert.True(await routing.DeleteAsync([OldPaths[0], notes], ct));
        Assert.Null(await local.ReadTextFileAsync(notes, ct));
    }

    // -- Commands that must run in a run, and where a preview's files are -------

    [Fact]
    public async Task A_preview_step_in_its_state_directory_runs_in_the_runs_container()
    {
        await CreateRunAsync();
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var state = routing.PreviewStateDirectoryFor(WorkingDirectory);

        using var step = await routing.StartCommandAsync(
            RunCommand.Prepared(Command(state + "/npm-cache", "npm", "ci")) with { RunIn = _run }, TestContext.Current.CancellationToken);
        await step.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"{RunPaths.RunRoot(_run)}/preview-state", state);
        Assert.Empty(local.Started);
        Assert.Equal(Name(_run), _api.Execs.Last().Container);
    }

    [Theory]
    [InlineData("/tmp/preview/0123abcd")]
    [InlineData("/data/worktrees/.preview-volumes/11111111-1111-1111-1111-111111111111/data")]
    [InlineData("/ild-run/22222222222222222222222222222222/repo")]
    [InlineData("{run}/../22222222222222222222222222222222/repo")]
    public async Task A_command_that_must_run_in_a_run_is_refused_outside_it_and_never_starts_on_ilds_machine(string directory)
    {
        await CreateRunAsync();
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var execs = _api.Execs.Count;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => routing.StartCommandAsync(
            RunCommand.Prepared(Command(directory.Replace("{run}", RunPaths.RunRoot(_run)), "sh")) with { RunIn = _run },
            TestContext.Current.CancellationToken));

        Assert.StartsWith("refusing to run sh", error.Message);
        Assert.Empty(local.Started);
        Assert.Equal(execs, _api.Execs.Count);
    }

    [Fact]
    public async Task A_command_for_a_run_the_host_does_not_hold_is_refused_and_not_started_on_ilds_machine()
    {
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        var gone = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => routing.StartCommandAsync(
            RunCommand.Prepared(Command(RunPaths.WorkingDirectory(gone), "sh")) with { RunIn = gone }, TestContext.Current.CancellationToken));

        Assert.Contains("never runs on ILD's own machine", error.Message);
        Assert.Empty(local.Started);
    }

    // -- Kept preview data: the run's own copy -----------------------------------

    private PreviewDataStore StoreWith(params (string Path, string Content)[] files)
    {
        var store = new PreviewDataStore(Path.Combine(_scratch, "preview-data"));
        foreach (var (path, content) in files)
        {
            var file = Path.Combine(store.FolderOf(Repo), path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }
        return store;
    }

    private string Stored(PreviewDataStore store, string path) => File.ReadAllText(Path.Combine(store.FolderOf(Repo), path));

    private static byte[] TarOf(params (string Path, string Content)[] files)
    {
        using var archive = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(archive, leaveOpen: true))
            foreach (var (path, content) in files)
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
        return archive.ToArray();
    }

    private string Unpacked(byte[] archive, string path)
    {
        var folder = Path.Combine(_scratch, $"unpacked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        System.Formats.Tar.TarFile.ExtractToDirectory(new MemoryStream(archive), folder, overwriteFiles: false);
        return File.ReadAllText(Path.Combine(folder, path));
    }

    private static bool IsFill(FakeDockerExec e) => e.Joined.Contains("tar -x");

    private static bool IsPack(FakeDockerExec e) => e.Joined.Contains("tar -c");

    [Fact]
    public async Task A_run_made_with_kept_preview_data_gets_a_copy_of_the_stored_copy_in_its_own_folder()
    {
        _host.PreviewData = StoreWith(("data/seed", "kept"));
        byte[]? filled = null;
        _api.OnExec = async e =>
        {
            var stdin = e.WantsStdin ? await e.ReadStdinToEndAsync() : null;
            if (IsFill(e)) filled = stdin;
            // The run has no copy yet.
            await e.ExitAsync(e.Joined.Contains("[ -e") && e.Command.Contains(RunPaths.PreviewData(_run)) ? 1 : 0);
        };

        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo, KeepPreviewData: true), TestContext.Current.CancellationToken);

        var fill = Assert.Single(_api.Execs, IsFill);
        Assert.Equal(Name(_run), fill.Container);
        Assert.Contains(RunPaths.PreviewData(_run), fill.Command);
        Assert.Equal("kept", Unpacked(filled!, "data/seed"));
        // Nothing of ILD's own disk is mounted into the run.
        Assert.DoesNotContain(_api.Container(Name(_run))!["HostConfig"]!["Binds"]!.AsArray(), b => b!.GetValue<string>().Contains(_scratch));
    }

    [Fact]
    public async Task Ending_a_run_cleanly_puts_its_copy_back_in_the_store_and_the_copy_goes_with_the_container()
    {
        var store = StoreWith(("data/seed", "old"));
        _host.PreviewData = store;
        await CreateRunAsync();
        var archive = TarOf(("data/seed", "changed in the run"), ("data/added", "new"));
        _api.OnExec = async e =>
        {
            if (IsPack(e)) await e.WriteStdoutAsync(archive);
            await e.ExitAsync(0);
        };

        await _host.DeleteAsync(new RunEnvironment(_run, WorkingDirectory, Repo), TestContext.Current.CancellationToken);

        Assert.Contains(RunPaths.PreviewData(_run), Assert.Single(_api.Execs, IsPack).Command);
        Assert.Equal("changed in the run", Stored(store, "data/seed"));
        Assert.Equal("new", Stored(store, "data/added"));
        Assert.DoesNotContain(Name(_run), _api.Containers());
        Assert.DoesNotContain(Name(_run), _api.Volumes());
        Assert.Equal([Repo.ToString("D")], Directory.GetFileSystemEntries(Path.Combine(_scratch, "preview-data")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_run_whose_container_stopped_is_removed_without_putting_its_copy_back()
    {
        var store = StoreWith(("data/seed", "old"));
        _host.PreviewData = store;
        await CreateRunAsync();
        _api.Stop(Name(_run));
        _api.OnExec = async e =>
        {
            if (IsPack(e)) await e.WriteStdoutAsync(TarOf(("data/seed", "from a crashed run")));
            await e.ExitAsync(0);
        };

        await _host.DeleteAsync(new RunEnvironment(_run, WorkingDirectory, Repo), TestContext.Current.CancellationToken);

        Assert.Equal("old", Stored(store, "data/seed"));
        Assert.DoesNotContain(Name(_run), _api.Containers());
        Assert.DoesNotContain(Name(_run), _api.Volumes());
    }

    [Fact]
    public async Task Reclaiming_a_run_whose_container_stopped_neither_starts_it_again_nor_puts_its_copy_back()
    {
        var store = StoreWith(("data/seed", "old"));
        _host.PreviewData = store;
        await CreateRunAsync();
        _api.Stop(Name(_run));
        _api.Execs.Clear();
        _api.OnExec = async e =>
        {
            if (IsPack(e)) await e.WriteStdoutAsync(TarOf(("data/seed", "from a crashed run")));
            await e.ExitAsync(0);
        };
        var repositories = new Mock<IRepositoryManager>();
        var reclaimer = new RunReclaimer(repositories.Object, new Mock<IProviderStore>().Object, new RoutingRunHost(new FakeRunHost(), _host));
        var run = new ILD.Data.Entities.LoopRun { Id = _run, WorkItemId = "wi-1", WorktreePath = WorkingDirectory, RepositoryId = Repo };

        Assert.True(await reclaimer.ReclaimLocalStateAsync(run));

        Assert.Empty(_api.Execs);
        Assert.Equal("old", Stored(store, "data/seed"));
        Assert.DoesNotContain(Name(_run), _api.Containers());
    }

    [Fact]
    public async Task Reclaiming_a_running_run_puts_its_copy_back()
    {
        var store = StoreWith(("data/seed", "old"));
        _host.PreviewData = store;
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            if (IsPack(e)) await e.WriteStdoutAsync(TarOf(("data/seed", "changed in the run")));
            await e.ExitAsync(0);
        };
        var reclaimer = new RunReclaimer(new Mock<IRepositoryManager>().Object, new Mock<IProviderStore>().Object, new RoutingRunHost(new FakeRunHost(), _host));
        var run = new ILD.Data.Entities.LoopRun { Id = _run, WorkItemId = "wi-1", WorktreePath = WorkingDirectory, RepositoryId = Repo };

        Assert.True(await reclaimer.ReclaimLocalStateAsync(run));

        Assert.Equal("changed in the run", Stored(store, "data/seed"));
        Assert.DoesNotContain(Name(_run), _api.Containers());
    }

    [Theory]
    [InlineData(0, "this is not a tar archive")]
    [InlineData(1, "")]
    public async Task A_copy_that_cannot_be_put_back_leaves_the_stored_copy_as_it_was(int exitCode, string output)
    {
        var store = StoreWith(("data/seed", "old"));
        _host.PreviewData = store;
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            if (IsPack(e)) await e.WriteStdoutAsync(output);
            await e.ExitAsync(IsPack(e) ? exitCode : 0);
        };

        await _host.DeleteAsync(new RunEnvironment(_run, WorkingDirectory, Repo), TestContext.Current.CancellationToken);

        Assert.Equal("old", Stored(store, "data/seed"));
        Assert.Equal([Repo.ToString("D")], Directory.GetFileSystemEntries(Path.Combine(_scratch, "preview-data")).Select(Path.GetFileName));
        Assert.DoesNotContain(Name(_run), _api.Containers());
    }

    [Fact]
    public async Task The_routing_host_creates_loop_runs_in_containers_and_leaves_the_leftovers_of_both_hosts_to_their_own()
    {
        var routing = new RoutingRunHost(new FakeRunHost(), _host);
        var env = await CreateRunAsync(_host);
        var other = Guid.NewGuid();
        SeedRun(other, Instance);

        var leftovers = await routing.ListLeftoversAsync(new HashSet<Guid> { _run }, TestContext.Current.CancellationToken);

        Assert.Equal(RunHostKind.Container, routing.Kind);
        Assert.Equal(WorkingDirectory, env.WorkingDirectory);
        var leftover = Assert.Single(leftovers, l => l.Kind == ContainerRunHost.RunLeftoverKind);
        Assert.Equal(other.ToString("N"), leftover.Location);
        Assert.True(await routing.DeleteLeftoverAsync(leftover, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Name(other), _api.Containers());
    }

    [Fact]
    public async Task Chats_and_other_paths_stay_on_the_local_host_and_loop_runs_go_to_the_container()
    {
        await CreateRunAsync();
        _api.Execs.Clear();
        var local = new FakeRunHost().WithDirectory("/data/chat-sessions/c1");
        var routing = new RoutingRunHost(local, _host);

        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/c1", TestContext.Current.CancellationToken));
        Assert.Empty(_api.Execs);

        await routing.DirectoryExistsAsync(WorkingDirectory, TestContext.Current.CancellationToken);
        Assert.Single(_api.Execs);
    }

    [Fact]
    public async Task A_recreated_ild_container_is_put_back_on_each_runs_network_at_the_address_the_run_has_for_it()
    {
        _api.SeedIldContainer("ild-main");
        using var host = NewHost(Options(ildContainer: "ild-main"));
        await CreateRunAsync(host);
        var given = _api.Container(Name(_run))!["HostConfig"]!["ExtraHosts"]!.AsArray().Single()!.GetValue<string>();

        _api.SeedIldContainer("ild-main");
        Assert.Empty(_api.NetworksOf("ild-main"));
        using var restarted = NewHost(Options(ildContainer: "ild-main"));
        await restarted.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Name(_run)], _api.NetworksOf("ild-main"));
        Assert.Equal(given["ild.example:".Length..], _api.Connects[^1]["EndpointConfig"]!["IPAMConfig"]!["IPv4Address"]!.GetValue<string>());
        Assert.Contains(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("Reconnected ILD to the network of run", StringComparison.Ordinal));

        var connects = _api.Connects.Count;
        await restarted.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(connects, _api.Connects.Count);
    }

    [Fact]
    public async Task Every_run_chat_and_installer_network_gets_a_small_subnet_of_the_pool()
    {
        await CreateRunAsync();
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);

        foreach (var network in new[] { Name(_run), ChatContainer })
        {
            var subnet = _api.SubnetOf(network)!;
            Assert.StartsWith("10.213.", subnet, StringComparison.Ordinal);
            Assert.EndsWith("/28", subnet, StringComparison.Ordinal);
        }
        Assert.NotEqual(_api.SubnetOf(Name(_run)), _api.SubnetOf(ChatContainer));
    }

    [Fact]
    public async Task More_runs_than_the_engines_default_pool_has_room_for_all_get_a_network()
    {
        _api.DefaultPoolNetworkLimit = 3;
        var runs = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };

        var baseRepository = MakeBaseRepository();
        foreach (var run in runs) await CreateRunAsync(_host, run, baseRepository);

        Assert.All(runs, run => Assert.Contains(Name(run), _api.Networks()));
        Assert.Equal(runs.Count, runs.Select(r => _api.SubnetOf(Name(r))).Distinct().Count());
    }

    [Fact]
    public async Task A_subnet_a_foreign_network_holds_is_skipped()
    {
        _api.SeedForeignNetwork("lan", "10.213.0.0/24");

        await CreateRunAsync();

        Assert.Equal("10.213.1.0/28", _api.SubnetOf(Name(_run)));
    }

    [Fact]
    public async Task A_run_network_made_without_a_subnet_gets_a_plain_connect_and_a_warning_when_ild_comes_back()
    {
        _api.SeedIldContainer("ild-main");
        using var host = NewHost(Options(ildContainer: "ild-main"));
        await CreateRunAsync(host);
        _api.ForgetUserSubnet(Name(_run));

        _api.SeedIldContainer("ild-main");
        using var restarted = NewHost(Options(ildContainer: "ild-main"));
        await restarted.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Name(_run)], _api.NetworksOf("ild-main"));
        Assert.Null(_api.Connects[^1]["EndpointConfig"]);
        Assert.Contains(_log.Warnings, m => m.Contains(Name(_run), StringComparison.Ordinal) || m.Contains($"run {_run:N}", StringComparison.Ordinal));
        Assert.Contains(_log.Warnings, m => m.Contains("made again", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("10.50.0.0/16")]
    [InlineData("192.168.0.0/24")]
    public void The_subnet_pool_is_an_ipv4_network_of_at_least_a_slash_24(string pool)
        => Assert.Equal(pool, ContainerRunHostOptions.From(PoolSetting(pool)).SubnetPool);

    [Theory]
    [InlineData("10.50.0.0/28")]
    [InlineData("10.50.0.0")]
    [InlineData("fd00::/16")]
    [InlineData("soon")]
    public void A_subnet_pool_that_is_not_an_ipv4_network_of_at_least_a_slash_24_is_refused(string pool)
        => Assert.Contains("ILD_CONTAINER_SUBNET_POOL", Assert.Throws<InvalidOperationException>(() => ContainerRunHostOptions.From(PoolSetting(pool))).Message);

    [Fact]
    public void The_subnet_pool_defaults_to_a_private_range()
        => Assert.Equal("10.213.0.0/16", ContainerRunHostOptions.From(PoolSetting(null)).SubnetPool);

    private static Func<string, string?> PoolSetting(string? pool) => name => name switch
    {
        "ILD_API_URL" => "http://ild.example:8080",
        "ILD_CONTAINER_SOCKET" => "/var/run/docker.sock",
        "ILD_CONTAINER_RUNNER_IMAGE" => "img",
        "ILD_CONTAINER_SUBNET_POOL" => pool,
        _ => null,
    };

    // -- Toolchains --------------------------------------------------------------

    private static readonly RunAgent Claude = new(ManagedAgentCatalog.ClaudeCode, "2.1.0");

    private const string AgentNodeLine = """{"kind":"agentnode","spec":"node@24","version":"24.22.2","path":["/opt/ild-tools/mise/installs/node/24/bin"],"env":{}}""";
    private const string NodeLine = """{"kind":"tool","spec":"node@20","version":"20.19.1","path":["/opt/ild-toolchains/mise/installs/node/20.19.1/bin"],"env":{}}""";
    private static readonly string PrettierFolder = RunToolStore.RepositoryFolder([new RunTool("npm:prettier", "3")], false);
    private static string PrettierLine(string folder)
        => $$$"""{"kind":"tool","spec":"npm:prettier@3","version":"3.3.0","path":["{{{folder}}}/mise/installs/npm-prettier/3.3.0/bin"],"env":{}}""";
    private const string PrettierToml = "[tools]\n\"npm:prettier\" = \"3\"\n";

    private const string ClaudeLine = """{"kind":"agent","key":"claude-code","version":"2.1.0","path":["/opt/ild-tools/agents/claude-code/2.1.0/wrap"]}""";

    // Answers each of the store's installers with the lines of the real script's output that are in the zone it installs
    // into (the installer's ILD_TOOLS_ROOT), and the run's own commands with success.
    private List<IReadOnlyList<string>> AnswerInstaller(params string[] lines)
    {
        var installs = new List<IReadOnlyList<string>>();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                lock (installs) installs.Add(e.Command);
                Assert.True(_api.Container(e.Container) is not null, "the installer runs while it exists");
                var root = _api.Container(e.Container)!["Env"]!.AsArray().Select(v => v!.GetValue<string>()).Single(v => v.StartsWith("ILD_TOOLS_ROOT=", StringComparison.Ordinal))["ILD_TOOLS_ROOT=".Length..];
                await e.WriteStdoutAsync(string.Join('\n', lines.Where(l => l.Contains($"\"{root}/", StringComparison.Ordinal))) + "\n");
            }
            await e.ExitAsync(0);
        };
        return installs;
    }

    private Task<RunEnvironment> CreateRunAsync(ContainerRunHost host, Guid run, string basePath)
        => host.CreateAsync(new RunEnvironmentRequest(run, basePath, "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Create_installs_the_repositorys_toolchains_and_ilds_agents_into_the_store_and_mounts_it_read_only()
    {
        using var host = NewHost(Options(), [Claude]);
        var installs = AnswerInstaller(AgentNodeLine, NodeLine, ClaudeLine);
        var repo = MakeBaseRepository("base", ("package.json", "{}"), (".nvmrc", "v20\n"));

        await CreateRunAsync(host, _run, repo);

        Assert.Equal(2, installs.Count);
        Assert.Equal(["agentnode=node@24", "agent=claude-code|@anthropic-ai/claude-code|2.1.0|claude"], installs.Single(i => i.Contains("agentnode=node@24")).TakeLast(2));
        Assert.Equal(["tool=node@20"], installs.Single(i => i.Contains("tool=node@20")).TakeLast(1));
        foreach (var installer in _api.Created.Where(c => c.StartsWith("containers/ild-tools-", StringComparison.Ordinal)))
            Assert.DoesNotContain(installer["containers/".Length..], _api.Containers());
        Assert.Contains("volumes/ild-tools-agents", _api.Created);
        Assert.Contains("volumes/ild-tools-toolchains", _api.Created);

        var container = _api.Container(Name(_run))!;
        Assert.Equal(["ild-tools-agents:/opt/ild-tools:ro", "ild-tools-toolchains:/opt/ild-toolchains:ro"], BindsOf(container).Skip(1).Order(StringComparer.Ordinal));
        var env = container["Env"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        var home = RunPaths.HomeDirectory(_run);
        Assert.Contains($"PATH=/opt/ild-toolchains/mise/installs/node/20.19.1/bin:/opt/ild-tools/agents/claude-code/2.1.0/wrap:{home}/.local/bin:/opt/ild-tools/mise/installs/node/24/bin:{RunToolEnvironment.ImagePath}", env);
        Assert.Contains($"NPM_CONFIG_PREFIX={home}/.local", env);
    }

    [Fact]
    public async Task An_installers_network_left_without_its_container_is_a_leftover_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "ild-tools-" + Guid.NewGuid().ToString("N");
        _api.SeedToolsNetwork(name, Instance);

        var leftover = Assert.Single(await _host.ListLeftoversAsync(new HashSet<Guid>(), ct));

        Assert.Equal(name, leftover.Location);
        Assert.True(await _host.DeleteLeftoverAsync(leftover, ct));
        Assert.Empty(_api.Networks());
    }

    [Fact]
    public async Task The_installer_mounts_only_its_zone_writable_as_the_runner_with_no_privileges_and_no_run_label()
    {
        using var host = NewHost(Options(), [Claude]);
        string? seen = null;
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                var config = _api.Container(e.Container)!;
                seen = config.ToJsonString();
                Assert.Equal("10001:10001", config["User"]!.GetValue<string>());
                Assert.Equal(["ild-tools-agents:/opt/ild-tools:rw"], BindsOf(config));
                Assert.Equal(["ALL"], config["HostConfig"]!["CapDrop"]!.AsArray().Select(c => c!.GetValue<string>()));
                Assert.Equal(e.Container, config["HostConfig"]!["NetworkMode"]!.GetValue<string>());
                Assert.Null(config["Labels"]![RunPaths.RunLabel]);
                Assert.Contains("MISE_DATA_DIR=/opt/ild-tools/mise", config["Env"]!.AsArray().Select(v => v!.GetValue<string>()));
                await e.WriteStdoutAsync(ClaudeLine + "\n");
            }
            await e.ExitAsync(0);
        };

        await CreateRunAsync(host, _run, MakeBaseRepository());

        Assert.NotNull(seen);
    }

    private static string EnvOf(System.Text.Json.Nodes.JsonObject container, string name)
        => container["Env"]!.AsArray().Select(v => v!.GetValue<string>()).Single(v => v.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    private static bool IgnoresScripts(System.Text.Json.Nodes.JsonObject container)
        => container["Env"]!.AsArray().Select(v => v!.GetValue<string>()).Contains("npm_config_ignore_scripts=true");

    [Fact]
    public async Task The_installer_is_on_an_internal_network_of_its_own_that_ild_joins_and_goes_through_the_proxy_under_its_zone()
    {
        _api.SeedIldContainer("ild-main");
        using var host = NewHost(Options(ildContainer: "ild-main"), [Claude]);
        string? network = null;
        System.Text.Json.Nodes.JsonObject? config = null;
        string[] ildOnIt = [];
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                network = e.Container;
                config = _api.Container(e.Container);
                ildOnIt = [.. _api.NetworksOf("ild-main")];
                Assert.True(_api.Network(e.Container)!["Internal"]!.GetValue<bool>());
                await e.WriteStdoutAsync(ClaudeLine + "\n");
            }
            await e.ExitAsync(0);
        };

        await CreateRunAsync(host, _run, MakeBaseRepository());

        Assert.NotNull(config);
        Assert.Contains(network!, ildOnIt);
        var hostConfig = config["HostConfig"]!;
        Assert.Equal(network, hostConfig["NetworkMode"]!.GetValue<string>());
        Assert.Contains(hostConfig["ExtraHosts"]!.AsArray(), h => h!.GetValue<string>().StartsWith("ild.example:10.213.", StringComparison.Ordinal));
        Assert.Equal(["127.0.0.1"], hostConfig["Dns"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Null(hostConfig["Tmpfs"]);

        var proxy = EnvOf(config, "HTTPS_PROXY");
        Assert.EndsWith("@ild.example:3128", proxy);
        Assert.True(AgentIsolationEgressTests.TryReadUrl(EgressProxyTests.Creds, proxy, out var scope));
        Assert.Equal(new LaunchScope(LaunchKind.Installer, Installer: "agents"), scope);
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            Assert.Equal(proxy, EnvOf(config, name));
        Assert.Equal("1", EnvOf(config, "NODE_USE_ENV_PROXY"));
        Assert.Equal(AgentIsolation.EgressNoProxy, EnvOf(config, "NO_PROXY"));
        Assert.Equal(AgentIsolation.EgressNoProxy, EnvOf(config, "no_proxy"));

        // Gone with its installer, and ILD is off it.
        Assert.Contains($"networks/{network}", _api.Deleted);
        Assert.DoesNotContain(network!, _api.Networks());
        Assert.DoesNotContain(network!, _api.NetworksOf("ild-main"));
    }

    [Fact]
    public async Task A_repositorys_own_tools_go_in_a_volume_of_its_own_that_only_its_installer_and_its_runs_mount()
    {
        using var host = NewHost(Options(), [Claude]);
        var snapshots = new Dictionary<string, System.Text.Json.Nodes.JsonObject>();
        var installs = AnswerInstaller(AgentNodeLine, ClaudeLine, NodeLine, PrettierLine(PrettierFolder));
        var inner = _api.OnExec!;
        _api.OnExec = e =>
        {
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal)) snapshots[e.Container] = _api.Container(e.Container)!;
            return inner(e);
        };
        var repo = MakeBaseRepository("base", ("package.json", "{}"), ("mise.toml", PrettierToml));

        await CreateRunAsync(host, _run, repo);

        Assert.Equal(3, installs.Count);
        var volume = $"ild-tools-repo-{Repo:N}";
        Assert.Contains($"volumes/{volume}", _api.Created);
        var byZone = snapshots.Values.ToDictionary(c => EnvOf(c, "ILD_TOOLS_ROOT"));
        Assert.Equal([$"ild-tools-agents:/opt/ild-tools:rw"], BindsOf(byZone[RunToolStore.Root]));
        Assert.Equal([$"ild-tools-toolchains:/opt/ild-toolchains:rw"], BindsOf(byZone[RunToolStore.ToolchainsRoot]));
        // The repository's installer reaches its own volume, and the shared toolchains it builds with read-only: never the agents'.
        Assert.Equal([$"{volume}:/opt/ild-repo-tools:rw", "ild-tools-toolchains:/opt/ild-toolchains:ro"], BindsOf(byZone[PrettierFolder]));
        Assert.StartsWith("/opt/ild-toolchains/mise/installs/node/20.19.1/bin:", EnvOf(byZone[PrettierFolder], "PATH"), StringComparison.Ordinal);

        var run = _api.Container(Name(_run))!;
        Assert.Contains($"{volume}:/opt/ild-repo-tools:ro", BindsOf(run));
        Assert.All(BindsOf(run).Skip(1), b => Assert.EndsWith(":ro", b, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_agents_zone_is_never_the_volume_of_the_one_store_from_before_the_zones()
    {
        using var host = NewHost(Options(), [Claude]);
        var snapshots = new List<System.Text.Json.Nodes.JsonObject>();
        AnswerInstaller(AgentNodeLine, ClaudeLine, NodeLine);
        var inner = _api.OnExec!;
        _api.OnExec = e =>
        {
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal)) lock (snapshots) snapshots.Add(_api.Container(e.Container)!);
            return inner(e);
        };

        await CreateRunAsync(host, _run, MakeBaseRepository("base", ("package.json", "{}")));
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("ild-tools", _api.Volumes());
        var binds = snapshots.Concat([_api.Container(Name(_run))!, _api.Container(ChatContainer)!]).SelectMany(c => BindsOf(c));
        Assert.DoesNotContain(binds, b => b.StartsWith("ild-tools:", StringComparison.Ordinal));
        Assert.Contains("ild-tools-agents:/opt/ild-tools:ro", BindsOf(_api.Container(Name(_run))!));
    }

    [Fact]
    public async Task Another_repositorys_run_does_not_mount_the_first_repositorys_volume()
    {
        using var host = NewHost(Options(), []);
        AnswerInstaller(NodeLine, PrettierLine(PrettierFolder));
        var repo = MakeBaseRepository("base", ("package.json", "{}"), ("mise.toml", PrettierToml));
        var other = MakeBaseRepository("other", ("package.json", "{}"));
        var otherRun = Guid.NewGuid();

        await CreateRunAsync(host, _run, repo);
        await host.CreateAsync(new RunEnvironmentRequest(otherRun, other, "b", OtherRepo), TestContext.Current.CancellationToken);

        Assert.Contains(BindsOf(_api.Container(Name(_run))!), b => b.StartsWith($"ild-tools-repo-{Repo:N}:", StringComparison.Ordinal));
        Assert.DoesNotContain(BindsOf(_api.Container(Name(otherRun))!), b => b.Contains("repo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_scripts_are_off_in_the_agents_and_toolchain_zones_and_on_in_a_repositorys_unless_it_opts_out()
    {
        using var host = NewHost(Options(), [Claude]);
        var snapshots = new List<System.Text.Json.Nodes.JsonObject>();
        AnswerInstaller(AgentNodeLine, ClaudeLine, NodeLine, PrettierLine(PrettierFolder));
        var inner = _api.OnExec!;
        _api.OnExec = e =>
        {
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal)) lock (snapshots) snapshots.Add(_api.Container(e.Container)!);
            return inner(e);
        };

        await CreateRunAsync(host, _run, MakeBaseRepository("base", ("package.json", "{}"), ("mise.toml", PrettierToml)));

        Assert.Equal(3, snapshots.Count);
        foreach (var installer in snapshots)
            Assert.Equal(EnvOf(installer, "ILD_TOOLS_ROOT") != PrettierFolder, IgnoresScripts(installer));
    }

    [Fact]
    public async Task A_chat_container_never_mounts_a_repositorys_own_tools_and_a_chat_does_not_install_them()
    {
        using var host = NewHost(Options(), []);
        var installs = AnswerInstaller(NodeLine, PrettierLine(PrettierFolder));
        var repo = MakeBaseRepository("base", ("package.json", "{}"), ("mise.toml", PrettierToml));

        var chat = Guid.NewGuid();
        await host.CreateChatFolderAsync(chat, "/unused", TestContext.Current.CancellationToken);
        await host.AddChatToolsAsync(ChatFolder(chat), repo, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(installs, i => i.Any(a => a.Contains("npm:", StringComparison.Ordinal)));
        Assert.DoesNotContain(BindsOf(_api.Container(ChatContainer)!), b => b.Contains("repo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_run_that_needs_the_same_gets_it_without_an_installer()
    {
        using var host = NewHost(Options(), [Claude]);
        var installs = AnswerInstaller(NodeLine, ClaudeLine);
        var repo = MakeBaseRepository("base", ("package.json", "{}"), (".nvmrc", "20"));

        await CreateRunAsync(host, _run, repo);
        var other = Guid.NewGuid();
        await CreateRunAsync(host, other, repo);

        Assert.Equal(2, installs.Count);
        Assert.Contains("ild-tools-agents:/opt/ild-tools:ro", _api.Container(Name(other))!["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));
    }

    [Fact]
    public async Task An_install_that_fails_fails_the_run_saying_what_and_why_and_leaves_no_container()
    {
        using var host = NewHost(Options(), []);
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                await e.WriteStderrAsync("mise ERROR no version of node matches 99\n");
                await e.ExitAsync(1);
                return;
            }
            await e.ExitAsync(0);
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunAsync(host, _run, MakeBaseRepository("base", ("package.json", "{}"), (".nvmrc", "99"))));

        Assert.Contains("node@99", ex.Message);
        Assert.Contains("no version of node matches 99", ex.Message);
        Assert.Empty(_api.Containers());
        Assert.Equal(["ild-tools-toolchains"], _api.Volumes());
    }

    [Fact]
    public async Task With_toolchains_off_a_run_has_only_its_image_no_store_and_no_path_of_ilds()
    {
        var options = Options() with { Toolchains = false };
        using var host = NewHost(options, [Claude]);
        var installs = AnswerInstaller(NodeLine, ClaudeLine);

        await CreateRunAsync(host, _run, MakeBaseRepository("base", ("package.json", "{}")));

        Assert.Empty(installs);
        Assert.DoesNotContain("ild-tools-agents", _api.Volumes());
        var env = _api.Container(Name(_run))!["Env"]!.AsArray().Select(e => e!.GetValue<string>());
        Assert.DoesNotContain(env, e => e.StartsWith("PATH=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_needs_nothing_from_the_store_does_not_touch_it_but_can_still_install_globally()
    {
        var installs = AnswerInstaller();

        await CreateRunAsync(_host, _run, MakeBaseRepository());

        Assert.Empty(installs);
        Assert.DoesNotContain("ild-tools-agents", _api.Volumes());
        var env = _api.Container(Name(_run))!["Env"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains($"NPM_CONFIG_PREFIX={RunPaths.HomeDirectory(_run)}/.local", env);
        Assert.Contains($"PATH={RunPaths.HomeDirectory(_run)}/.local/bin:{RunToolEnvironment.ImagePath}", env);
    }

    [Fact]
    public async Task A_command_never_carries_ilds_own_path_into_the_run()
    {
        await CreateRunAsync();
        var commands = new List<string>();
        _api.OnExec = async e =>
        {
            lock (commands) commands.Add(e.Joined);
            await e.ExitAsync(0);
        };
        var psi = Command(WorkingDirectory, "npm", "test");
        psi.Environment["PATH"] = "/ild/side/bin:/usr/bin";

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains(commands, c => c.Contains("npm test", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("/ild/side/bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_variable_the_caller_sets_on_purpose_reaches_the_run_and_its_path_goes_in_front_of_the_runs_never_in_place_of_it()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "npm", "install");
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = "/caller/bin:/usr/bin",
            ["NPM_CONFIG_PREFIX"] = "/workspace/.npm-global",
            ["NPM_CONFIG_CACHE"] = "/state/npm-cache",
        };

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, environment: environment), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        Assert.Contains("NPM_CONFIG_CACHE=/state/npm-cache", exec.Env);
        // The run's own prefix was set when it was made; the host owns it.
        Assert.DoesNotContain(exec.Env, c => c.StartsWith("NPM_CONFIG_PREFIX=", StringComparison.Ordinal));
        // The run's PATH is the container's: the wrapper puts the caller's entries in front of it, and no exec variable replaces it.
        Assert.DoesNotContain(exec.Env, c => c.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Contains("/caller/bin:/usr/bin", exec.Command);
    }

    // -- Socket ------------------------------------------------------------------

    [Fact]
    public async Task A_rootful_podman_socket_is_warned_about_with_its_path()
    {
        _api.LibpodInfo = JsonNode.Parse("""{"host":{"security":{"rootless":false}}}""");

        var warning = await _host.WarningAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_api.SocketPath, warning);
        Assert.Contains("not rootless", warning);
    }

    [Fact]
    public async Task A_rootful_docker_socket_is_warned_about()
    {
        _api.Info = JsonNode.Parse("""{"SecurityOptions":["name=seccomp,profile=builtin","name=cgroupns"]}""");

        Assert.NotNull(await _host.WarningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_rootless_socket_is_not_warned_about()
    {
        _api.LibpodInfo = JsonNode.Parse("""{"host":{"security":{"rootless":true}}}""");
        Assert.Null(await _host.WarningAsync(TestContext.Current.CancellationToken));

        _api.LibpodInfo = null;
        _api.Info = JsonNode.Parse("""{"SecurityOptions":["name=seccomp,profile=builtin","name=rootless"]}""");
        Assert.Null(await _host.WarningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_engine_that_answers_but_cannot_say_whether_it_is_rootless_is_not_warned_about()
    {
        Assert.Null(await _host.WarningAsync(TestContext.Current.CancellationToken));

        _api.Info = JsonNode.Parse("""{"Name":"engine without security options"}""");
        Assert.Null(await _host.WarningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_socket_ild_cannot_reach_is_warned_about_with_its_path_and_why()
    {
        var path = Path.Combine(_scratch, "nothing.sock");
        using var unreachable = NewHost(Options() with { Socket = path });

        var warning = await unreachable.WarningAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(warning);
        Assert.Contains(path, warning);
        Assert.Contains("cannot reach", warning);
        Assert.DoesNotContain("docker:80", warning);
    }

    [Fact]
    public async Task The_unreachable_warning_goes_once_the_socket_answers()
    {
        var path = Path.Combine(_scratch, "later.sock");
        using var host = NewHost(Options() with { Socket = path });
        Assert.NotNull(await host.WarningAsync(TestContext.Current.CancellationToken));

        File.CreateSymbolicLink(path, _api.SocketPath);

        Assert.Null(await host.WarningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_startup_check_logs_the_warning_for_a_rootful_socket_and_is_quiet_otherwise()
    {
        await _host.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_log.Warnings);

        _api.LibpodInfo = JsonNode.Parse("""{"host":{"security":{"rootless":false}}}""");
        await _host.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Contains(_log.Warnings, w => w.Contains("not rootless"));
    }

    [Fact]
    public async Task The_startup_check_logs_a_socket_it_cannot_reach()
    {
        using var unreachable = NewHost(Options() with { Socket = Path.Combine(_scratch, "nothing.sock") });

        await unreachable.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_log.Warnings, w => w.Contains("cannot reach"));
    }

    // -- Settings ----------------------------------------------------------------

    [Theory]
    [InlineData(null, "ILD_API_URL")]
    [InlineData("http://localhost:5000", "host name")]
    [InlineData("http://127.0.0.1:5000", "host name")]
    [InlineData("http://10.0.0.5:5000", "host name")]
    public void Settings_refuse_an_ild_address_the_run_cannot_be_given_a_name_for(string? apiUrl, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContainerRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => apiUrl,
            "ILD_CONTAINER_SOCKET" => "/run/user/1000/podman/podman.sock",
            "ILD_CONTAINER_RUNNER_IMAGE" => "img",
            _ => null,
        }));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("ILD_CONTAINER_SOCKET")]
    [InlineData("ILD_CONTAINER_RUNNER_IMAGE")]
    public void Settings_require_the_socket_and_the_runner_image(string missing)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContainerRunHostOptions.From(name => name == missing ? null : name switch
        {
            "ILD_API_URL" => "http://ild:8080",
            "ILD_CONTAINER_SOCKET" => "/run/podman.sock",
            "ILD_CONTAINER_RUNNER_IMAGE" => "img",
            _ => null,
        }));

        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Settings_take_the_socket_as_a_path_or_a_unix_url_and_never_a_relative_path()
    {
        string? Setting(string socket, string name) => name switch
        {
            "ILD_API_URL" => "http://ild:8080",
            "ILD_CONTAINER_SOCKET" => socket,
            "ILD_CONTAINER_RUNNER_IMAGE" => "img",
            _ => null,
        };

        Assert.Equal("/run/podman.sock", ContainerRunHostOptions.From(n => Setting("unix:///run/podman.sock", n)).Socket);
        Assert.Contains("absolute", Assert.Throws<InvalidOperationException>(() => ContainerRunHostOptions.From(n => Setting("podman.sock", n))).Message);
    }

    [Fact]
    public void Settings_default_to_the_limits_of_the_kubernetes_host_and_a_default_instance()
    {
        var options = ContainerRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => "http://ild:8080",
            "ILD_CONTAINER_SOCKET" => "/run/podman.sock",
            "ILD_CONTAINER_RUNNER_IMAGE" => "img",
            _ => null,
        });

        Assert.Equal("default", options.Instance);
        Assert.Equal(4_000_000_000L, options.NanoCpus);
        Assert.Equal(8L * 1024 * 1024 * 1024, options.MemoryBytes);
        Assert.Null(options.IldContainer);
    }

    [Theory]
    [InlineData("4", 4_000_000_000L)]
    [InlineData("1.5", 1_500_000_000L)]
    [InlineData("500m", 500_000_000L)]
    [InlineData("0", null)]
    public void A_cpu_limit_is_read_as_a_kubernetes_quantity(string quantity, long? expected)
        => Assert.Equal(expected, ContainerRunHostOptions.NanoCpusOf(quantity));

    [Theory]
    [InlineData("8Gi", 8L * 1024 * 1024 * 1024)]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("1G", 1_000_000_000L)]
    [InlineData("1048576", 1_048_576L)]
    [InlineData("0", null)]
    public void A_memory_limit_is_read_as_a_kubernetes_quantity(string quantity, long? expected)
        => Assert.Equal(expected, ContainerRunHostOptions.BytesOf(quantity));

    [Theory]
    [InlineData("lots")]
    [InlineData("-1")]
    public void A_limit_that_is_not_a_quantity_is_refused_by_name(string quantity)
    {
        Assert.Contains("ILD_RUN_CPU_LIMIT", Assert.Throws<InvalidOperationException>(() => ContainerRunHostOptions.NanoCpusOf(quantity)).Message);
        Assert.Contains("ILD_RUN_MEMORY_LIMIT", Assert.Throws<InvalidOperationException>(() => ContainerRunHostOptions.BytesOf(quantity)).Message);
    }

    [Fact]
    public void The_kubernetes_host_reads_the_shared_limits_first_and_its_own_older_names_after()
    {
        string? Setting(string cpu, string name) => name switch
        {
            "ILD_API_URL" => "http://ild:8080",
            "ILD_K8S_RUNNER_IMAGE" => "img",
            "ILD_K8S_CPU_LIMIT" => "9",
            "ILD_RUN_CPU_LIMIT" => cpu,
            "ILD_K8S_MEMORY_LIMIT" => "3Gi",
            _ => null,
        };

        var shared = ILD.Core.Services.RunHosts.Kubernetes.KubernetesRunHostOptions.From(n => Setting("2", n));
        var older = ILD.Core.Services.RunHosts.Kubernetes.KubernetesRunHostOptions.From(n => Setting("", n));

        Assert.Equal("2", shared.CpuLimit);
        Assert.Equal("9", older.CpuLimit);
        Assert.Equal("3Gi", shared.MemoryLimit);
    }

    [Fact]
    public async Task The_sweep_removes_installer_containers_a_killed_ild_left()
    {
        _api.SeedSignIn("ild-tools-left", Instance, role: "tools");
        _api.SeedSignIn("ild-tools-other", "other", role: "tools");
        using var host = NewHost(Options());
        var sweeper = new RunHostSweeper(new RoutingRunHost(new FakeRunHost(), host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));

        await sweeper.SweepAsync(host, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("ild-tools-left", _api.Containers());
        Assert.Contains("ild-tools-other", _api.Containers());
    }

    [Fact]
    public async Task Shutdown_removes_the_installer_containers_that_are_open()
    {
        using var host = NewHost(Options(), [Claude]);
        var ct = TestContext.Current.CancellationToken;
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Container.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                await host.ShutdownAsync(ct);
                await e.WriteStdoutAsync(AgentNodeLine + "\n");
            }
            await e.ExitAsync(0);
        };

        await host.CreateAsync(new RunEnvironmentRequest(Guid.NewGuid(), MakeBaseRepository("shut", ("package.json", "{}")), "b", Repo), ct);

        Assert.Contains(_api.Deleted, d => d.StartsWith("containers/ild-tools-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ILD_CONTAINER_INSTANCE")]
    [InlineData("ILD_CONTAINER_TOOLS_VOLUME")]
    public void A_name_the_engine_would_refuse_is_refused_when_the_settings_are_read(string setting)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ContainerRunHostOptions.From(name => name switch
        {
            "ILD_CONTAINER_SOCKET" => "/var/run/docker.sock",
            "ILD_CONTAINER_RUNNER_IMAGE" => "img",
            "ILD_API_URL" => "http://ild:8080",
            _ when name == setting => "bad name!",
            _ => null,
        }));

        Assert.Contains(setting, ex.Message);
    }

    [Fact]
    public async Task A_failed_create_on_an_existing_environment_keeps_the_container_and_volume_it_found()
    {
        await CreateRunAsync();
        _api.Stop(Name(_run));
        _api.OnExec = e => e.ExitAsync(1);

        await Assert.ThrowsAnyAsync<Exception>(() => _host.CreateAsync(
            new RunEnvironmentRequest(_run, MakeBaseRepository("base-again"), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken));

        Assert.Contains(Name(_run), _api.Containers());
        Assert.Contains(Name(_run), _api.Volumes());
        Assert.DoesNotContain($"containers/{Name(_run)}", _api.Deleted);
    }

    [Fact]
    public async Task A_failed_create_of_a_new_environment_removes_what_it_made()
    {
        _api.OnExec = e => e.ExitAsync(1);

        await Assert.ThrowsAnyAsync<Exception>(() => _host.CreateAsync(
            new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(Name(_run), _api.Containers());
        Assert.DoesNotContain(Name(_run), _api.Volumes());
        Assert.DoesNotContain(Name(_run), _api.Networks());
    }

    [Fact]
    public async Task Create_on_an_environment_that_holds_a_checkout_does_not_clone_into_it_again()
    {
        await CreateRunAsync();
        _api.Stop(Name(_run));
        var before = _api.Execs.Count;
        _api.OnExec = e => e.ExitAsync(0);

        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository("base-again"), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        var after = _api.Execs.Skip(before).ToList();
        Assert.Contains(after, e => e.Command.Any(c => c.Contains("$1/.git")));
        Assert.DoesNotContain(after, e => e.WantsStdin);
        Assert.DoesNotContain(after, e => e.Command.Any(c => c.Contains("checkout")));
    }

    // -- Shutdown ----------------------------------------------------------------

    [Fact]
    public async Task Shutdown_does_not_throw_when_the_run_listing_fails()
    {
        await CreateRunAsync();
        _api.ContainerListStatus = 500;

        await _host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_does_not_throw_when_the_run_listing_is_not_json()
    {
        await CreateRunAsync();
        _api.ContainerListBody = "not json";

        await _host.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_does_not_throw_when_it_is_cancelled()
    {
        await CreateRunAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await _host.ShutdownAsync(cancelled.Token);
    }

    [Fact]
    public async Task Shutdown_removes_the_chat_container_and_stops_each_run_which_starts_again_on_its_next_command()
    {
        await CreateRunAsync();
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;

        await _host.ShutdownAsync(ct);

        Assert.Contains($"containers/{ChatContainer}", _api.Deleted);
        Assert.True(_api.IsStopped(Name(_run)));
        // The run keeps everything it holds: its container, volume and network are not removed.
        Assert.DoesNotContain($"containers/{Name(_run)}", _api.Deleted);
        Assert.Contains(Name(_run), _api.Volumes());

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run), ct);
        await process.WaitForExitAsync(ct);

        Assert.False(_api.IsStopped(Name(_run)));
        Assert.Contains(_api.Execs, e => e.Container == Name(_run) && e.Command.Contains("make-it"));
    }

    // Podman refuses an exec into a stopped container with a 500, Docker with a 409.
    [Theory]
    [InlineData(500)]
    [InlineData(409)]
    public async Task A_run_container_stopped_by_a_restart_of_the_engine_is_started_again_by_its_next_command(int refusal)
    {
        await CreateRunAsync();
        _api.Stop(Name(_run));
        _api.StoppedExecStatus = refusal;
        var ct = TestContext.Current.CancellationToken;

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run), ct);
        await process.WaitForExitAsync(ct);

        Assert.Equal(0, process.ExitCode);
        Assert.False(_api.IsStopped(Name(_run)));
    }

    // Docker switches the exec connection to the stream with a 101, Podman answers 200.
    [Theory]
    [InlineData(101)]
    [InlineData(200)]
    public async Task A_streaming_command_reads_its_output_whichever_status_the_engine_starts_the_stream_with(int status)
    {
        await CreateRunAsync();
        _api.HijackStatus = status;
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("streamed\n");
            await e.ExitAsync(0);
        };

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run), TestContext.Current.CancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("streamed\n", output);
    }

    [Fact]
    public async Task The_fake_engine_lists_a_stopped_container_only_when_asked_for_all()
    {
        await CreateRunAsync();
        _api.Stop(Name(_run));
        using var client = new DockerApiClient(_api.SocketPath);
        var ct = TestContext.Current.CancellationToken;

        var running = (await client.GetAsync("/containers/json", ct))!.AsArray();
        var all = (await client.GetAsync("/containers/json?all=true", ct))!.AsArray();

        Assert.DoesNotContain(running, c => c!["Names"]![0]!.GetValue<string>() == "/" + Name(_run));
        Assert.Contains(all, c => c!["Names"]![0]!.GetValue<string>() == "/" + Name(_run));
    }

    [Fact]
    public async Task Checking_a_folder_in_a_stopped_run_container_starts_it_again_so_an_editor_can_attach()
    {
        await CreateRunAsync();
        _api.Stop(Name(_run));

        Assert.True(await _host.DirectoryExistsAsync(WorkingDirectory, TestContext.Current.CancellationToken));

        Assert.False(_api.IsStopped(Name(_run)));
    }

    [Fact]
    public async Task A_chat_container_made_from_another_runner_image_is_replaced_at_startup_and_one_from_the_same_is_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        _api.ImageIds["registry.example/ild-runner:1"] = "sha256:aaa";
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        var made = _api.Container(ChatContainer)!["Labels"]![ContainerRunHost.SpecLabel]!.GetValue<string>();

        using (var same = NewHost(Options()))
        {
            await same.CheckAsync(ct);
            Assert.DoesNotContain($"containers/{ChatContainer}", _api.Deleted);
        }

        // compose up --build: the same name, a new image.
        _api.ImageIds["registry.example/ild-runner:1"] = "sha256:bbb";
        using var upgraded = NewHost(Options());
        await upgraded.CheckAsync(ct);
        Assert.Contains($"containers/{ChatContainer}", _api.Deleted);

        await upgraded.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        Assert.NotEqual(made, _api.Container(ChatContainer)!["Labels"]![ContainerRunHost.SpecLabel]!.GetValue<string>());
    }

    // -- Chats -------------------------------------------------------------------

    private const string ChatContainer = "ild-chat-main";

    private static string ChatFolder(Guid chat) => RunPaths.ChatRoot(chat);

    private static IEnumerable<string> BindsOf(JsonObject container)
        => container["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>());

    [Fact]
    public async Task The_first_chat_creates_the_shared_chat_container_once_with_a_volume_an_internal_network_and_the_store_read_only()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var firstPath = await _host.CreateChatFolderAsync(first, "/unused", TestContext.Current.CancellationToken);
        var secondPath = await _host.CreateChatFolderAsync(second, "/unused", TestContext.Current.CancellationToken);

        Assert.Equal(ChatFolder(first), firstPath);
        Assert.Equal(ChatFolder(second), secondPath);
        Assert.Equal(
            [$"volumes/{ChatContainer}", "volumes/ild-tools-agents", "volumes/ild-tools-toolchains", $"networks/{ChatContainer}", $"containers/{ChatContainer}"],
            _api.Created);
        var container = _api.Container(ChatContainer)!;
        Assert.Equal("registry.example/ild-runner:1", container["Image"]!.GetValue<string>());
        Assert.Equal("10001:10001", container["User"]!.GetValue<string>());
        Assert.Equal([$"{ChatContainer}:/ild-chat:rw", "ild-tools-agents:/opt/ild-tools:ro", "ild-tools-toolchains:/opt/ild-toolchains:ro"], BindsOf(container));
        var host = container["HostConfig"]!;
        Assert.Equal(ChatContainer, host["NetworkMode"]!.GetValue<string>());
        Assert.Equal("ALL", host["CapDrop"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("no-new-privileges", host["SecurityOpt"]!.AsArray().Single()!.GetValue<string>());
        Assert.Null(host["Memory"]);
        Assert.Null(host["NanoCpus"]);
        Assert.True(_api.Network(ChatContainer)!["Internal"]!.GetValue<bool>());
        Assert.Equal("Ada", container["Env"]!.AsArray().Select(e => e!.GetValue<string>()).Single(e => e.StartsWith("GIT_AUTHOR_NAME=", StringComparison.Ordinal))["GIT_AUTHOR_NAME=".Length..]);
    }

    [Fact]
    public async Task The_chat_container_has_no_run_label_so_the_sweep_never_takes_it_for_a_runs()
    {
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);

        foreach (var labels in new[] { _api.Container(ChatContainer)!["Labels"]!, _api.Network(ChatContainer)!["Labels"]! })
        {
            Assert.Equal("ild", labels["app.kubernetes.io/managed-by"]!.GetValue<string>());
            Assert.Equal(Instance, labels["ild/instance"]!.GetValue<string>());
            Assert.Equal("chat", labels["ild/role"]!.GetValue<string>());
            Assert.Null(labels["ild/run"]);
        }

        var stale = Guid.NewGuid();
        SeedRun(stale, Instance);
        var leftovers = await _host.ListLeftoversAsync(new HashSet<Guid>(), TestContext.Current.CancellationToken);
        var sweeper = new RunHostSweeper(new RoutingRunHost(new FakeRunHost(), _host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));
        await sweeper.SweepAsync(_host, TestContext.Current.CancellationToken);

        Assert.Equal(stale.ToString("N"), Assert.Single(leftovers).Location);
        Assert.Contains(ChatContainer, _api.Containers());
        Assert.Contains(ChatContainer, _api.Volumes());
        Assert.Contains(ChatContainer, _api.Networks());
    }

    [Fact]
    public async Task A_chats_folder_gets_its_home_made_in_the_chat_container()
    {
        var chat = Guid.NewGuid();

        await _host.CreateChatFolderAsync(chat, "/unused", TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        Assert.Equal(ChatContainer, exec.Container);
        Assert.Contains(RunPaths.ChatHome(chat), exec.Command);
    }

    [Fact]
    public async Task A_stopped_chat_container_is_made_again_on_next_use_and_the_volume_keeps_the_chats()
    {
        var chat = Guid.NewGuid();
        await _host.CreateChatFolderAsync(chat, "/unused", TestContext.Current.CancellationToken);
        _api.Stop(ChatContainer);

        Assert.True(await _host.DirectoryExistsAsync(ChatFolder(chat), TestContext.Current.CancellationToken));

        Assert.Equal(2, _api.Created.Count(c => c == $"containers/{ChatContainer}"));
        Assert.Equal(1, _api.Created.Count(c => c == $"volumes/{ChatContainer}"));
        Assert.Equal(ChatContainer, _api.Execs.Last().Container);
    }

    [Fact]
    public async Task A_chat_command_runs_in_the_chat_container_as_that_chat_with_its_home_and_the_agents_on_the_path()
    {
        using var host = NewHost(Options(), [Claude]);
        AnswerInstaller(AgentNodeLine, ClaudeLine);
        var chat = Guid.NewGuid();
        var folder = $"{ChatFolder(chat)}/repos/widgets";

        using var process = await host.StartCommandAsync(RunCommand.Prepared(Command(folder, "git", "status"), chat), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        var home = RunPaths.ChatHome(chat);
        Assert.Equal(ChatContainer, exec.Container);
        Assert.Equal($"HOME={home}", exec.Env.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.Equal(
            $"PATH=/opt/ild-tools/agents/claude-code/2.1.0/wrap:{home}/.local/bin:/opt/ild-tools/mise/installs/node/24/bin:{RunToolEnvironment.ImagePath}",
            exec.Env.Single(c => c.StartsWith("PATH=", StringComparison.Ordinal)));
        Assert.Contains(folder, exec.Command);
        Assert.Equal(["git", "status"], exec.Command.TakeLast(2));
        Assert.Contains($"{chat:N}/", exec.Command.Single(c => c.StartsWith(chat.ToString("N") + "/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_chat_command_gets_the_chats_npm_prefix_whatever_the_caller_sets()
    {
        var chat = Guid.NewGuid();
        var psi = Command(ChatFolder(chat), "npm", "i", "-g", "x");

        using var process = await _host.StartCommandAsync(
            RunCommand.Prepared(psi, chat, new Dictionary<string, string?> { ["NPM_CONFIG_PREFIX"] = "/caller/prefix" }), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"NPM_CONFIG_PREFIX={RunPaths.ChatHome(chat)}/.local", _api.Execs.Last().Env.Single(c => c.StartsWith("NPM_CONFIG_PREFIX=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_command_in_a_chat_folder_is_the_chats_even_when_its_caller_names_another_id_to_kill_it_by()
    {
        var chat = Guid.NewGuid();
        var title = Guid.NewGuid();

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(ChatFolder(chat), "claude"), title), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        Assert.Equal(ChatContainer, exec.Container);
        Assert.Equal($"HOME={RunPaths.ChatHome(chat)}", exec.Env.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.Contains(exec.Command, c => c.StartsWith(title.ToString("N") + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_toolchains_of_the_repositories_a_chat_clones_are_installed_and_put_on_its_path_after_a_restart_too()
    {
        using var host = NewHost(Options(), []);
        var installs = AnswerInstaller(NodeLine);
        var chat = Guid.NewGuid();
        var repo = MakeBaseRepository("base", ("package.json", "{}"), (".nvmrc", "v20\n"));

        await host.AddChatToolsAsync(ChatFolder(chat), repo, TestContext.Current.CancellationToken);

        Assert.Equal("tool=node@20", Assert.Single(installs).Last());
        using var restarted = NewHost(Options(), []);
        using var process = await restarted.StartCommandAsync(RunCommand.Prepared(Command(ChatFolder(chat), "node", "-v"), chat), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var path = _api.Execs.Last().Env.Single(c => c.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.StartsWith("PATH=/opt/ild-toolchains/mise/installs/node/20.19.1/bin:", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_repository_wanting_another_version_of_a_tool_the_chat_has_does_not_change_it_and_says_so()
    {
        using var host = NewHost(Options(), []);
        var installs = AnswerInstaller(NodeLine);
        var chat = Guid.NewGuid();

        await host.AddChatToolsAsync(ChatFolder(chat), MakeBaseRepository("one", ("package.json", "{}"), (".nvmrc", "v20\n")), TestContext.Current.CancellationToken);
        await host.AddChatToolsAsync(ChatFolder(chat), MakeBaseRepository("two", ("package.json", "{}"), (".nvmrc", "v22\n")), TestContext.Current.CancellationToken);

        Assert.Single(installs);
        Assert.Contains(_log.Warnings, w => w.Contains("node@20", StringComparison.Ordinal) && w.Contains("node@22", StringComparison.Ordinal));
        Assert.Equal(["\"node@20\""], File.ReadAllText(Path.Combine(_scratch, "chat-tools", $"{chat:N}.json")).Trim('[', ']').Split(','));
    }

    [Fact]
    public async Task Deleting_a_chats_folder_removes_it_in_the_chat_container_and_forgets_its_toolchains()
    {
        using var host = NewHost(Options(), []);
        AnswerInstaller(NodeLine);
        var chat = Guid.NewGuid();
        await host.AddChatToolsAsync(ChatFolder(chat), MakeBaseRepository("base", ("package.json", "{}"), (".nvmrc", "v20\n")), TestContext.Current.CancellationToken);

        Assert.True(await host.DeleteAsync([ChatFolder(chat)], TestContext.Current.CancellationToken));

        var exec = _api.Execs.Last();
        Assert.Equal(ChatContainer, exec.Container);
        Assert.Contains(ChatFolder(chat), exec.Command);
        Assert.False(File.Exists(Path.Combine(_scratch, "chat-tools", $"{chat:N}.json")));
    }

    [Fact]
    public async Task Kill_all_for_a_chat_ends_what_runs_in_the_chat_container()
    {
        var chat = Guid.NewGuid();
        await _host.CreateChatFolderAsync(chat, "/unused", TestContext.Current.CancellationToken);
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("1\n");
            await e.ExitAsync(0);
        };

        var killed = await _host.KillAllAsync(chat, TestContext.Current.CancellationToken);

        Assert.Equal(1, killed);
        var kill = _api.Execs.Last();
        Assert.Equal(ChatContainer, kill.Container);
        Assert.Equal(ExecRunHost.KillAllScript, kill.Command[2]);
        Assert.Equal(chat.ToString("N"), kill.Command[^1]);
    }

    [Fact]
    public async Task Kill_all_for_a_chat_when_there_is_no_chat_container_asks_nothing_of_the_engine()
    {
        Assert.Equal(0, await _host.KillAllAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Empty(_api.Execs);
        Assert.DoesNotContain(ChatContainer, _api.Containers());
    }

    // -- Idle chat container -----------------------------------------------------

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private (ContainerRunHost Host, ManualTimeProvider Clock) IdleHost(TimeSpan? timeout = null)
    {
        var clock = new ManualTimeProvider();
        return (NewHost(Options() with { ChatIdleTimeout = timeout ?? IdleTimeout }, time: clock), clock);
    }

    [Fact]
    public async Task An_idle_chat_container_is_removed_after_the_timeout_and_its_volume_and_network_stay_and_the_next_chat_makes_it_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        var chat = Guid.NewGuid();
        await host.CreateChatFolderAsync(chat, "/unused", ct);

        clock.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);
        Assert.Contains(ChatContainer, _api.Containers());

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);

        Assert.DoesNotContain(ChatContainer, _api.Containers());
        Assert.Contains(ChatContainer, _api.Volumes());
        Assert.Contains(ChatContainer, _api.Networks());
        Assert.Contains(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("Removed the chat container", StringComparison.Ordinal));

        Assert.True(await host.DirectoryExistsAsync(ChatFolder(chat), ct));

        Assert.Contains(ChatContainer, _api.Containers());
        Assert.Equal(2, _api.Created.Count(c => c == $"containers/{ChatContainer}"));
        Assert.Equal(1, _api.Created.Count(c => c == $"volumes/{ChatContainer}"));
        Assert.Contains(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("is back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_chat_container_is_not_removed_while_a_chat_command_runs_or_a_chat_hold_is_open_and_the_time_counts_from_when_they_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        var chat = Guid.NewGuid();
        await host.CreateChatFolderAsync(chat, "/unused", ct);
        var release = new TaskCompletionSource();
        _api.OnExec = async e =>
        {
            await release.Task;
            await e.ExitAsync(0);
        };

        var running = host.StartCommandAsync(RunCommand.Prepared(Command(ChatFolder(chat), "npm", "run", "dev"), chat), ct);
        using var process = await running;
        clock.Advance(IdleTimeout * 2);
        await host.ReapIdleChatAsync(ct);
        Assert.Contains(ChatContainer, _api.Containers());

        release.SetResult();
        await process.WaitForExitAsync(ct);
        using (host.HoldChat(ChatFolder(chat)))
        {
            clock.Advance(IdleTimeout * 2);
            await host.ReapIdleChatAsync(ct);
            Assert.Contains(ChatContainer, _api.Containers());
        }

        clock.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);
        Assert.Contains(ChatContainer, _api.Containers());

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);
        Assert.DoesNotContain(ChatContainer, _api.Containers());
    }

    [Fact]
    public async Task A_chat_that_starts_while_the_reaper_removes_the_chat_container_waits_for_it_and_gets_a_new_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        var chat = Guid.NewGuid();
        await host.CreateChatFolderAsync(chat, "/unused", ct);
        var removing = new TaskCompletionSource();
        var proceed = new TaskCompletionSource();
        _api.BeforeRequest = async request =>
        {
            if (request.StartsWith("DELETE", StringComparison.Ordinal) && request.Contains($"/containers/{ChatContainer}", StringComparison.Ordinal) && !removing.Task.IsCompleted)
            {
                removing.SetResult();
                await proceed.Task;
            }
        };
        clock.Advance(IdleTimeout);

        var reaping = host.ReapIdleChatAsync(ct);
        await removing.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var starting = host.DirectoryExistsAsync(ChatFolder(chat), ct);
        await Task.Delay(100, ct);
        Assert.False(starting.IsCompleted);
        proceed.SetResult();
        await reaping;

        Assert.True(await starting);
        Assert.Contains(ChatContainer, _api.Containers());
        Assert.Equal(2, _api.Created.Count(c => c == $"containers/{ChatContainer}"));
    }

    [Fact]
    public async Task Run_containers_are_never_touched_by_the_idle_removal_whatever_state_their_run_is_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        var running = Guid.NewGuid();
        var awaitingHuman = Guid.NewGuid();
        var parked = Guid.NewGuid();
        foreach (var run in new[] { running, awaitingHuman, parked }) SeedRun(run, Instance);
        _api.Stop(Name(parked));
        var execs = _api.Execs.Count;
        var requests = _api.Requests.Count;

        clock.Advance(IdleTimeout * 10);
        await host.ReapIdleChatAsync(ct);

        Assert.DoesNotContain(ChatContainer, _api.Containers());
        foreach (var run in new[] { running, awaitingHuman, parked })
        {
            Assert.Contains(Name(run), _api.Containers());
            Assert.Contains(Name(run), _api.Volumes());
            Assert.DoesNotContain(_api.Requests.Skip(requests), r => r.Contains(Name(run), StringComparison.Ordinal));
        }
        Assert.True(_api.IsStopped(Name(parked)));
        Assert.Equal(execs, _api.Execs.Count);
        Assert.DoesNotContain($"containers/{Name(running)}", _api.Deleted);
    }

    [Fact]
    public async Task The_chat_container_stays_when_the_idle_timeout_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost(TimeSpan.Zero);
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);

        clock.Advance(TimeSpan.FromDays(30));
        await host.ReapIdleChatAsync(ct);

        Assert.Contains(ChatContainer, _api.Containers());
        Assert.DoesNotContain($"containers/{ChatContainer}", _api.Deleted);
    }

    [Fact]
    public async Task The_sweep_removes_an_idle_chat_container()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        var sweeper = new RunHostSweeper(new RoutingRunHost(new FakeRunHost(), host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));

        clock.Advance(IdleTimeout);
        await sweeper.SweepAsync(host, ct);

        Assert.DoesNotContain(ChatContainer, _api.Containers());
    }

    [Fact]
    public void The_idle_timeout_is_thirty_minutes_unless_set_and_zero_turns_it_off()
    {
        ContainerRunHostOptions From(string? minutes) => ContainerRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => "http://ild.example:8080",
            "ILD_CONTAINER_SOCKET" => "/var/run/docker.sock",
            "ILD_CONTAINER_RUNNER_IMAGE" => "img",
            "ILD_CHAT_IDLE_TIMEOUT_MINUTES" => minutes,
            _ => null,
        });

        Assert.Equal(TimeSpan.FromMinutes(30), From(null).ChatIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), From("5").ChatIdleTimeout);
        Assert.Equal(TimeSpan.Zero, From("0").ChatIdleTimeout);
        Assert.Throws<InvalidOperationException>(() => From("-1"));
        Assert.Throws<InvalidOperationException>(() => From("soon"));
    }

    [Fact]
    public async Task A_recreated_ild_container_is_put_back_on_the_chat_network_at_the_address_the_chat_container_has_for_it()
    {
        _api.SeedIldContainer("ild-main");
        using var host = NewHost(Options(ildContainer: "ild-main"));
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        var given = _api.Container(ChatContainer)!["HostConfig"]!["ExtraHosts"]!.AsArray().Single()!.GetValue<string>();

        _api.SeedIldContainer("ild-main");
        using var restarted = NewHost(Options(ildContainer: "ild-main"));
        await restarted.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal([ChatContainer], _api.NetworksOf("ild-main"));
        Assert.Equal(given["ild.example:".Length..], _api.Connects[^1]["EndpointConfig"]!["IPAMConfig"]!["IPv4Address"]!.GetValue<string>());
        Assert.Contains(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("Reconnected ILD to the network of the chat container", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_command_with_no_working_directory_for_a_chat_goes_to_the_chat_container_as_that_chat()
    {
        var chat = Guid.NewGuid();
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, _host);
        await routing.CreateChatFolderAsync(chat, "/data/chat-sessions", TestContext.Current.CancellationToken);

        using var process = await routing.StartCommandAsync(
            RunCommand.Prepared(Command("", "ls"), chat), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(local.Started);
        var exec = _api.Execs.Last();
        Assert.Equal(ChatContainer, exec.Container);
        Assert.Contains($"HOME={RunPaths.ChatHome(chat)}", exec.Env);
    }

    [Fact]
    public async Task The_routing_host_keeps_chats_in_the_container_and_chats_from_before_on_the_local_host()
    {
        var chat = Guid.NewGuid();
        var local = new FakeRunHost().WithDirectory("/data/chat-sessions/old");
        var routing = new RoutingRunHost(local, _host);

        var path = await routing.CreateChatFolderAsync(chat, "/data/chat-sessions", TestContext.Current.CancellationToken);
        Assert.Equal(ChatFolder(chat), path);
        Assert.Empty(local.ChatFolders);

        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/old", TestContext.Current.CancellationToken));
        Assert.Equal(ChatContainer, _api.Execs.Last().Container);
        var before = _api.Execs.Count;
        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/old", TestContext.Current.CancellationToken));
        Assert.Equal(before, _api.Execs.Count);

        await routing.DeleteAsync([ChatFolder(chat), "/data/chat-sessions/old"], TestContext.Current.CancellationToken);
        Assert.Equal(ChatContainer, _api.Execs.Last().Container);
        Assert.Contains(RunPaths.ChatRoot(chat), _api.Execs.Last().Command);
    }

    [Fact]
    public async Task Kill_all_does_not_start_a_stopped_chat_container()
    {
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        _api.Stop(ChatContainer);
        var created = _api.Created.Count;

        Assert.Equal(0, await _host.KillAllAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        Assert.Equal(created, _api.Created.Count);
    }

    // -- Provider sign-in --------------------------------------------------------

    private static string SignInContainerOf(FakeDockerApi api)
        => api.Created.Single(c => c.StartsWith("containers/ild-signin-", StringComparison.Ordinal))["containers/".Length..];

    [Fact]
    public async Task Every_kind_of_container_is_made_with_the_hardening_and_the_network_of_its_kind()
    {
        using var host = NewHost(Options(), [Claude]);
        AnswerInstaller(AgentNodeLine, ClaudeLine);
        // An installer is gone once its work is done, so each container's request is kept the first time it is used.
        var made = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var answer = _api.OnExec;
        _api.OnExec = e =>
        {
            if (_api.Container(e.Container) is { } config) made.TryAdd(e.Container, config.ToJsonString());
            return answer(e);
        };

        await CreateRunAsync(host, _run, MakeBaseRepository());
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        await using var signIn = await host.OpenSignInAsync(
            ManagedAgentCatalog.ClaudeCode, "/data/agents/claude/bin/claude", ["setup-token"], 100, 30, TestContext.Current.CancellationToken);

        var kinds = made.Keys.Select(name => name switch
        {
            _ when name == Name(_run) => "run",
            ChatContainer => "chat",
            _ when name.StartsWith("ild-tools-", StringComparison.Ordinal) => "installer",
            _ when name.StartsWith("ild-signin-", StringComparison.Ordinal) => "sign-in",
            _ => name,
        }).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["chat", "installer", "run", "sign-in"], kinds);

        foreach (var (name, json) in made)
        {
            var config = JsonNode.Parse(json)!;
            var hostConfig = config["HostConfig"]!;
            Assert.Equal(["ALL"], hostConfig["CapDrop"]!.AsArray().Select(c => c!.GetValue<string>()));
            Assert.Equal(["no-new-privileges"], hostConfig["SecurityOpt"]!.AsArray().Select(c => c!.GetValue<string>()));
            Assert.Equal("10001:10001", config["User"]!.GetValue<string>());
            Assert.Equal("registry.example/ild-runner:1", config["Image"]!.GetValue<string>());

            // A run, the chat container and an installer are on a network of their own that reaches only ILD; a sign-in
            // is on the engine's default network, to sign in.
            var ownNetwork = !name.StartsWith("ild-signin-", StringComparison.Ordinal);
            Assert.Equal(ownNetwork ? name : null, hostConfig["NetworkMode"]?.GetValue<string>());
            Assert.Equal(ownNetwork, hostConfig["ExtraHosts"] is not null);
        }
    }

    [Fact]
    public async Task The_sign_in_container_has_the_default_network_the_store_read_only_and_nothing_of_a_chat_or_run()
    {
        using var host = NewHost(Options(), [Claude]);
        AnswerInstaller(AgentNodeLine, ClaudeLine);
        string? seen = null;
        var answer = _api.OnExec;
        _api.OnExec = e =>
        {
            if (e.Container.StartsWith("ild-signin-", StringComparison.Ordinal)) seen = _api.Container(e.Container)!.ToJsonString();
            return answer(e);
        };

        await using var terminal = await host.OpenSignInAsync(
            ManagedAgentCatalog.ClaudeCode, "/data/agents/claude/bin/claude", ["setup-token"], 100, 30, TestContext.Current.CancellationToken);

        var name = SignInContainerOf(_api);
        var config = _api.Container(name)!;
        Assert.Null(config["HostConfig"]!["NetworkMode"]);
        Assert.Null(config["HostConfig"]!["ExtraHosts"]);
        Assert.Equal(["ild-tools-agents:/opt/ild-tools:ro"], config["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));
        Assert.Equal(["ALL"], config["HostConfig"]!["CapDrop"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(["no-new-privileges"], config["HostConfig"]!["SecurityOpt"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("10001:10001", config["User"]!.GetValue<string>());
        Assert.Equal("signin", config["Labels"]!["ild/role"]!.GetValue<string>());
        Assert.Null(config["Labels"]![RunPaths.RunLabel]);
        Assert.Empty(_api.Networks());
        Assert.DoesNotContain(_api.Volumes(), v => v != "ild-tools-agents");
        Assert.Contains("PATH=/opt/ild-tools/agents/claude-code/2.1.0/wrap:/tmp/home/.local/bin:/opt/ild-tools/mise/installs/node/24/bin:" + RunToolEnvironment.ImagePath,
            config["Env"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.NotNull(seen);

        var exec = _api.Execs.Last();
        Assert.True(exec.Tty);
        Assert.Equal(["claude", "setup-token"], exec.Command.TakeLast(2));
        Assert.Contains("100x30", exec.Resizes);
    }

    [Fact]
    public async Task The_sign_in_container_is_removed_when_the_terminal_is_disposed()
    {
        using var host = NewHost(Options(), [Claude]);
        AnswerInstaller(ClaudeLine);

        var terminal = await host.OpenSignInAsync(ManagedAgentCatalog.ClaudeCode, "claude", [], 80, 24, TestContext.Current.CancellationToken);
        var name = SignInContainerOf(_api);
        Assert.Contains(name, _api.Containers());

        await terminal.DisposeAsync();
        await terminal.DisposeAsync();

        Assert.DoesNotContain(name, _api.Containers());
    }

    [Fact]
    public async Task The_sign_in_container_is_removed_when_it_fails_to_start()
    {
        using var slow = NewHost(Options() with { ReadyTimeout = TimeSpan.FromMilliseconds(100) }, [Claude]);
        AnswerInstaller(ClaudeLine);
        _api.ContainerReadsBeforeRunning = int.MaxValue;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => slow.OpenSignInAsync(ManagedAgentCatalog.ClaudeCode, "claude", [], 80, 24, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(_api.Containers(), c => c.StartsWith("ild-signin-", StringComparison.Ordinal));
    }

    private async Task<SignInTerminal> OpenReadingAsync(ContainerRunHost host, Func<FakeDockerExec, Task> answer)
    {
        AnswerInstaller(ClaudeLine);
        var installer = _api.OnExec;
        _api.OnExec = e => e.Container.StartsWith("ild-signin-", StringComparison.Ordinal) && !e.Tty ? answer(e) : installer(e);
        return await host.OpenSignInAsync(ManagedAgentCatalog.ClaudeCode, "claude", [], 80, 24, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_login_file_is_read_out_of_the_sign_in_container_as_text()
    {
        using var host = NewHost(Options(), [Claude]);
        await using var terminal = await OpenReadingAsync(host, async e =>
        {
            await e.WriteStdoutAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"a":1}""")));
            await e.ExitAsync(0);
        });

        var text = await terminal.ReadLoginAsync(".claude/.credentials.json", TestContext.Current.CancellationToken);

        Assert.Equal("""{"a":1}""", text);
        var script = _api.Execs.Last().Command;
        Assert.Contains("! -L", script[2]);
        Assert.Contains("-le \"$2\"", script[2]);
        Assert.Equal([".claude/.credentials.json", "262144"], script.TakeLast(2));
    }

    [Fact]
    public async Task A_link_or_an_oversized_login_file_is_not_read()
    {
        using var host = NewHost(Options(), [Claude]);
        // The script's own refusal (not a regular file, or over the cap) is its exit status 3: nothing is printed.
        await using var terminal = await OpenReadingAsync(host, e => e.ExitAsync(3));

        Assert.Null(await terminal.ReadLoginAsync(".codex/auth.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_sign_in_login_read_that_fails_otherwise_is_an_error()
    {
        using var host = NewHost(Options(), [Claude]);
        await using var terminal = await OpenReadingAsync(host, async e =>
        {
            await e.WriteStderrAsync("base64: boom");
            await e.ExitAsync(1);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => terminal.ReadLoginAsync(".codex/auth.json", TestContext.Current.CancellationToken));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task The_sweep_removes_sign_in_containers_a_killed_ild_left_but_not_open_ones_or_other_installs()
    {
        _api.SeedSignIn("ild-signin-left", Instance);
        _api.SeedSignIn("ild-signin-other", "other");
        using var host = NewHost(Options(), [Claude]);
        AnswerInstaller(ClaudeLine);
        await using var open = await host.OpenSignInAsync(ManagedAgentCatalog.ClaudeCode, "claude", [], 80, 24, TestContext.Current.CancellationToken);
        var openName = SignInContainerOf(_api);
        var sweeper = new RunHostSweeper(new RoutingRunHost(new FakeRunHost(), host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));

        await sweeper.SweepAsync(host, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("ild-signin-left", _api.Containers());
        Assert.Contains("ild-signin-other", _api.Containers());
        Assert.Contains(openName, _api.Containers());
    }

    [Fact]
    public async Task A_sign_in_container_whose_removal_failed_is_left_for_the_sweep()
    {
        using var host = NewHost(Options(), [Claude]);
        AnswerInstaller(ClaudeLine);
        var terminal = await host.OpenSignInAsync(ManagedAgentCatalog.ClaudeCode, "claude", [], 80, 24, TestContext.Current.CancellationToken);
        var name = SignInContainerOf(_api);

        _api.Down = true;
        await terminal.DisposeAsync();
        _api.Down = false;

        var leftovers = await host.ListLeftoversAsync(new HashSet<Guid>(), TestContext.Current.CancellationToken);
        Assert.Contains(leftovers, l => l.Location == name);
    }
}
