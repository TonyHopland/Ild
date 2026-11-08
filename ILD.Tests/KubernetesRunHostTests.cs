using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Kubernetes;
using ILD.Core.Services.RunHosts.Tools;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace ILD.Tests;

public sealed class KubernetesRunHostTests : IAsyncLifetime
{
    private const string Instance = "main";

    private readonly Guid _run = Guid.NewGuid();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"ild-k8s-{Guid.NewGuid():N}");
    private readonly RecordingLogger<KubernetesRunHost> _log = new();
    private FakeKubernetesApi _api = null!;
    private KubernetesRunHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_scratch);
        _api = await FakeKubernetesApi.StartAsync();
        _host = NewHost(Options());
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    private static KubernetesRunHostOptions Options(string instance = Instance, string? toolsClaim = null) => KubernetesRunHostOptions.From(name => name switch
    {
        "ILD_API_URL" => "http://ild.example:8080",
        "ILD_K8S_RUNNER_IMAGE" => "registry.example/ild-runner:1",
        "ILD_K8S_INSTANCE" => instance,
        "ILD_K8S_STORAGE_CLASS" => "run-scratch",
        "ILD_K8S_TOOLS_CLAIM" => toolsClaim,
        "ILD_NETWORK_PROXY_PORT" => "3128",
        "GIT_AUTHOR_NAME" => "Ada",
        _ => null,
    }) with { ReadyTimeout = TimeSpan.FromSeconds(10) };

    private KubernetesRunHost NewHost(KubernetesRunHostOptions options, IReadOnlyList<RunAgent>? agents = null)
        => new(_api.Client(), options, FakeKubernetesApi.Namespace, new ProcessRunner(), _log) { ReadyPollInterval = TimeSpan.FromMilliseconds(10), Agents = () => agents ?? [], ChatToolsRoot = () => Path.Combine(_scratch, "chat-tools") };

    private string WorkingDirectory => KubernetesNames.WorkingDirectory(_run);

    private static string Name(Guid run) => KubernetesNames.ObjectName(run);

    private string MakeBaseRepository(params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_scratch, "base");
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(path, "a.txt"), "a");
        foreach (var (file, content) in files) File.WriteAllText(Path.Combine(path, file), content);
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

    private async Task<RunEnvironment> CreateRunAsync()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        return await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run"), TestContext.Current.CancellationToken);
    }

    private static ProcessStartInfo Command(string workingDirectory, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // -- Create ------------------------------------------------------------------

    [Fact]
    public async Task Create_makes_the_owner_first_and_every_other_object_names_it_as_owner_with_the_run_labels()
    {
        var env = await CreateRunAsync();

        Assert.Equal(WorkingDirectory, env.WorkingDirectory);
        Assert.Equal([$"secrets/{Name(_run)}", $"persistentvolumeclaims/{Name(_run)}", $"networkpolicies/{Name(_run)}", $"pods/{Name(_run)}"], _api.Created);

        var secret = _api.Get<V1Secret>("secrets", Name(_run))!;
        foreach (var meta in new[]
        {
            secret.Metadata,
            _api.Get<V1PersistentVolumeClaim>("persistentvolumeclaims", Name(_run))!.Metadata,
            _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Metadata,
            _api.Get<V1Pod>("pods", Name(_run))!.Metadata,
        })
        {
            Assert.Equal("ild", meta.Labels["app.kubernetes.io/managed-by"]);
            Assert.Equal(Instance, meta.Labels["ild/instance"]);
            Assert.Equal(_run.ToString("N"), meta.Labels["ild/run"]);
        }
        foreach (var meta in new[]
        {
            _api.Get<V1PersistentVolumeClaim>("persistentvolumeclaims", Name(_run))!.Metadata,
            _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Metadata,
            _api.Get<V1Pod>("pods", Name(_run))!.Metadata,
        })
        {
            var owner = Assert.Single(meta.OwnerReferences);
            Assert.Equal("Secret", owner.Kind);
            Assert.Equal(secret.Metadata.Name, owner.Name);
            Assert.Equal(secret.Metadata.Uid, owner.Uid);
        }
    }

    [Fact]
    public async Task Create_waits_for_the_pod_to_be_ready_before_putting_the_code_in()
    {
        _api.PodReadsBeforeReady = 3;
        await CreateRunAsync();

        Assert.Contains(_api.Execs, e => e.Joined.Contains("git clone"));
        Assert.All(_api.Execs, e => Assert.Equal("v5.channel.k8s.io", e.Protocol));
    }

    [Fact]
    public async Task Create_puts_the_base_repository_in_as_a_bundle_with_the_remote_as_origin_and_checks_out_the_run_branch()
    {
        var stdin = new Dictionary<FakeExec, byte[]>();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) stdin[e] = await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };

        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run"), TestContext.Current.CancellationToken);

        var clone = Assert.Single(_api.Execs, e => e.Joined.Contains("git clone"));
        Assert.Equal(WorkingDirectory, clone.Command[^2]);
        Assert.Equal("https://forge.example/acme/app.git", clone.Command[^1]);
        Assert.StartsWith("# v2 git bundle", Encoding.ASCII.GetString(stdin[clone], 0, 15));
        var checkout = Assert.Single(_api.Execs, e => e.Joined.Contains("git checkout"));
        Assert.Equal(["ild/wi-1-run"], checkout.Command.TakeLast(1));
    }

    [Fact]
    public async Task Create_gives_the_pod_no_privileges_a_burstable_size_and_the_chosen_volume()
    {
        await CreateRunAsync();

        var pod = _api.Get<V1Pod>("pods", Name(_run))!;
        Assert.False(pod.Spec.AutomountServiceAccountToken);
        Assert.True(pod.Spec.SecurityContext.RunAsNonRoot);
        Assert.Equal("RuntimeDefault", pod.Spec.SecurityContext.SeccompProfile.Type);
        var container = Assert.Single(pod.Spec.Containers);
        Assert.Equal("registry.example/ild-runner:1", container.Image);
        Assert.Equal(["/usr/bin/tini", "--", "sleep", "infinity"], container.Command);
        Assert.Equal("Ada", container.Env.Single(e => e.Name == "GIT_AUTHOR_NAME").Value);
        Assert.Equal(["ALL"], container.SecurityContext.Capabilities.Drop);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.Equal("100m", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("8Gi", container.Resources.Limits["memory"].ToString());
        Assert.NotEqual(container.Resources.Requests["memory"].ToString(), container.Resources.Limits["memory"].ToString());
        Assert.Equal("kubernetes.io/hostname", Assert.Single(pod.Spec.TopologySpreadConstraints).TopologyKey);

        var claim = _api.Get<V1PersistentVolumeClaim>("persistentvolumeclaims", Name(_run))!;
        Assert.Equal(["ReadWriteOnce"], claim.Spec.AccessModes);
        Assert.Equal("run-scratch", claim.Spec.StorageClassName);
        Assert.Equal("10Gi", claim.Spec.Resources.Requests["storage"].ToString());
    }

    [Fact]
    public async Task Create_leaves_the_cluster_to_choose_the_storage_class_when_none_is_set()
    {
        var options = KubernetesRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => "http://ild.example:8080",
            "ILD_K8S_RUNNER_IMAGE" => "registry.example/ild-runner:1",
            _ => null,
        });
        _host = NewHost(options);

        await CreateRunAsync();

        Assert.Null(_api.Get<V1PersistentVolumeClaim>("persistentvolumeclaims", Name(_run))!.Spec.StorageClassName);
    }

    [Fact]
    public async Task Create_lets_the_pod_reach_only_ild_and_dns_and_be_reached_only_by_ild()
    {
        await CreateRunAsync();

        var policy = _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Spec;
        Assert.Equal(["Ingress", "Egress"], policy.PolicyTypes);
        Assert.Equal(_run.ToString("N"), policy.PodSelector.MatchLabels["ild/run"]);
        var toIld = policy.Egress[0];
        Assert.Equal("ild", toIld.To.Single().PodSelector.MatchLabels["app.kubernetes.io/name"]);
        Assert.Equal([8080, 3128], toIld.Ports.Select(p => int.Parse(p.Port.Value)).ToArray());
        Assert.Equal([53, 53], policy.Egress[1].Ports.Select(p => int.Parse(p.Port.Value)).ToArray());
        Assert.Equal(2, policy.Egress.Count);
        Assert.Equal("ild", policy.Ingress.Single().FromProperty.Single().PodSelector.MatchLabels["app.kubernetes.io/name"]);
    }

    [Fact]
    public async Task Create_takes_a_pod_that_is_already_healthy_as_it_is()
    {
        await CreateRunAsync();
        _api.Created.Clear();
        _api.Execs.Clear();

        var again = await _host.CreateAsync(new RunEnvironmentRequest(_run, "/unused", "ild/wi-1-run"), TestContext.Current.CancellationToken);

        Assert.Equal(WorkingDirectory, again.WorkingDirectory);
        Assert.Empty(_api.Created);
        Assert.Empty(_api.Execs);
    }

    [Fact]
    public async Task Create_that_fails_to_put_the_code_in_removes_what_it_made_and_says_why()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.WriteStderrAsync("fatal: bad bundle\n");
            await e.ExitAsync(e.Joined.Contains("git clone") ? 128 : 0);
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b"), TestContext.Current.CancellationToken));

        Assert.Contains("fatal: bad bundle", ex.Message);
        Assert.Empty(_api.Names("pods"));
        Assert.Empty(_api.Names("secrets"));
    }

    // -- Toolchains --------------------------------------------------------------

    [Fact]
    public async Task With_a_tools_claim_the_store_is_filled_by_an_installer_pod_and_mounted_read_only_in_the_run()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);
        var installs = new List<(string Pod, IReadOnlyList<string> Command)>();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Pod.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                lock (installs) installs.Add((e.Pod, e.Command));
                var installer = _api.Get<V1Pod>("pods", e.Pod)!;
                Assert.Equal("ild-tools", installer.Spec.Volumes.Single().PersistentVolumeClaim.ClaimName);
                Assert.Null(installer.Spec.Volumes.Single().PersistentVolumeClaim.ReadOnlyProperty);
                Assert.False(installer.Metadata.Labels.ContainsKey(KubernetesNames.RunLabel));
                await e.WriteStdoutAsync("""
                    {"kind":"tool","spec":"node@24","version":"24.11.0","path":["/opt/ild-tools/mise/installs/node/24.11.0/bin"],"env":{}}
                    {"kind":"agent","key":"pi","version":"1.5.0","path":["/opt/ild-tools/agents/pi/1.5.0/wrap"]}

                    """);
            }
            await e.ExitAsync(0);
        };

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(("package.json", "{}")), "ild/wi-1-run"), TestContext.Current.CancellationToken);

        var (pod, command) = Assert.Single(installs);
        Assert.Equal(["agentnode=node@24", "tool=node@24", "agent=pi|@earendil-works/pi-coding-agent|1.5.0|pi"], command.TakeLast(3));
        Assert.Contains($"pods/{pod}", _api.Deleted);

        var run = _api.Get<V1Pod>("pods", Name(_run))!;
        var claim = run.Spec.Volumes.Single(v => v.PersistentVolumeClaim?.ClaimName == "ild-tools");
        Assert.True(claim.PersistentVolumeClaim.ReadOnlyProperty);
        var mount = run.Spec.Containers.Single().VolumeMounts.Single(m => m.Name == claim.Name);
        Assert.Equal(RunToolStore.Root, mount.MountPath);
        Assert.True(mount.ReadOnlyProperty);
        var path = run.Spec.Containers.Single().Env.Single(e => e.Name == "PATH").Value;
        Assert.StartsWith("/opt/ild-tools/mise/installs/node/24.11.0/bin:/opt/ild-tools/agents/pi/1.5.0/wrap:", path);
    }

    [Fact]
    public async Task Without_a_tools_claim_a_run_has_only_its_image_and_the_startup_check_says_so()
    {
        var host = NewHost(Options(), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(("package.json", "{}")), "ild/wi-1-run"), TestContext.Current.CancellationToken);
        await host.CheckAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_api.Created, c => c.StartsWith("pods/ild-tools-", StringComparison.Ordinal));
        var run = _api.Get<V1Pod>("pods", Name(_run))!;
        Assert.DoesNotContain(run.Spec.Containers.Single().Env, e => e.Name == "PATH");
        Assert.Contains(_log.Warnings, w => w.Contains("ILD_K8S_TOOLS_CLAIM"));
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
        psi.Environment["ILD_TEST_ONLY"] = "yes";

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hello\n", await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("warn\n", await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, process.ExitCode);
        var exec = _api.Execs.Last();
        Assert.Equal(Name(_run), exec.Pod);
        Assert.Contains("ILD_TEST_ONLY=yes", exec.Command);
        Assert.Equal($"HOME={KubernetesNames.HomeDirectory(_run)}", exec.Command.Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.DoesNotContain(exec.Command, c => c.StartsWith("PATH=", StringComparison.Ordinal));
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
        Assert.Contains("connection to the run's pod", await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_launch_runs_the_cli_by_name_as_the_pod_has_it_and_without_the_local_uid_crossing()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "/data/agents/claude-code/v1/node_modules/.bin/claude", "-p");
        psi.Environment["OPENAI_BASE_URL"] = "http://ild.example:8080/gateway";

        using var process = await _host.StartCommandAsync(
            RunCommand.Agent(psi, _run, null, NetworkMode.Off, new Dictionary<string, string> { ["FROM_CONTEXT"] = "1" }), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var command = _api.Execs.Last().Command;
        Assert.DoesNotContain("/usr/bin/setpriv", command);
        Assert.Equal(["claude", "-p"], command.TakeLast(2));
        Assert.Contains("FROM_CONTEXT=1", command);
        Assert.Contains("OPENAI_BASE_URL=http://ild.example:8080/gateway", command);
    }

    [Fact]
    public async Task Proxy_variables_that_name_ilds_loopback_name_ilds_address_for_the_pod()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "curl");
        psi.Environment["HTTP_PROXY"] = "http://scope:abc@127.0.0.1:3128";

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("HTTP_PROXY=http://scope:abc@ild.example:3128", _api.Execs.Last().Command);
    }

    [Fact]
    public async Task Files_an_agent_launch_reads_are_copied_into_the_pod_and_the_launch_is_pointed_at_the_copies()
    {
        await CreateRunAsync();
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-mcp-config"));
        File.WriteAllText(Path.Combine(readRoot, "ild-mcp-config", "claude-1.json"), "{\"mcp\":true}");
        var host = new KubernetesRunHost(_api.Client(), Options(), FakeKubernetesApi.Namespace, new ProcessRunner(), _log)
        {
            ReadyPollInterval = TimeSpan.FromMilliseconds(10),
            AgentReadRoot = () => readRoot,
        };
        var written = new Dictionary<string, string>();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && e.Command.Count > 4 && e.Command[3] == "sh" && e.Command[4].StartsWith("/run/ild/agent-read", StringComparison.Ordinal))
                written[e.Command[4]] = Encoding.UTF8.GetString(await e.ReadStdinToEndAsync());
            await e.ExitAsync(0);
        };
        var psi = Command(WorkingDirectory, "claude", "--mcp-config", Path.Combine(readRoot, "ild-mcp-config", "claude-1.json"));

        using var process = await host.StartCommandAsync(
            RunCommand.Agent(psi, _run, null, NetworkMode.Off, new Dictionary<string, string> { ["CFG"] = "--c=" + Path.Combine(readRoot, "ild-mcp-config", "claude-1.json") }),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var staged = "/run/ild/agent-read/ild-mcp-config/claude-1.json";
        Assert.Equal("{\"mcp\":true}", written[staged]);
        var launch = _api.Execs.Last().Command;
        Assert.Contains(staged, launch);
        Assert.Contains("CFG=--c=" + staged, launch);
        Assert.DoesNotContain(launch, c => c.Contains(readRoot, StringComparison.Ordinal));
    }

    // -- Kill leftovers ----------------------------------------------------------

    [Fact]
    public async Task Kill_all_ends_what_still_runs_in_the_pod_and_waits_for_it()
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
        Assert.Equal(Name(_run), kill.Pod);
        Assert.Equal(KubernetesRunHost.KillAllScript, kill.Command[2]);
    }

    [Fact]
    public async Task Kill_all_for_a_run_with_no_pod_asks_nothing_of_the_cluster_and_kills_nothing()
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

    // -- Delete and sweep --------------------------------------------------------

    [Fact]
    public async Task Delete_removes_only_the_owner_and_the_cluster_garbage_collects_the_rest()
    {
        var env = await CreateRunAsync();

        await _host.DeleteAsync(env, TestContext.Current.CancellationToken);

        Assert.Equal([$"secrets/{Name(_run)}"], _api.Deleted);
        Assert.Empty(_api.Names("pods"));
        Assert.Empty(_api.Names("persistentvolumeclaims"));
        Assert.Empty(_api.Names("networkpolicies"));
    }

    [Fact]
    public async Task Delete_of_a_run_that_is_already_gone_is_not_an_error()
    {
        await _host.DeleteAsync(new RunEnvironment(Guid.NewGuid(), "/ild-run/x/repo"), TestContext.Current.CancellationToken);

        Assert.Empty(_api.Deleted);
    }

    private void SeedRun(Guid run, string instance, bool secret = true, string managedBy = "ild")
    {
        if (secret) _api.Seed("secrets", Name(run), instance, run, managedBy);
        _api.Seed("pods", Name(run), instance, run, managedBy);
        _api.Seed("persistentvolumeclaims", Name(run), instance, run, managedBy);
        _api.Seed("networkpolicies", Name(run), instance, run, managedBy);
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
        SeedRun(orphan, Instance, secret: false);
        SeedRun(otherInstance, "other");
        SeedRun(notOurs, Instance, managedBy: "someone-else");
        var sweeper = new RunHostSweeper(new RoutingRunHost(FakeLocal(), _host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { live }), TimeSpan.FromMinutes(1));

        await sweeper.SweepAsync(_host, TestContext.Current.CancellationToken);

        foreach (var kind in new[] { "secrets", "pods", "persistentvolumeclaims", "networkpolicies" })
        {
            var left = _api.Names(kind);
            Assert.Contains(Name(live), left);
            Assert.Contains(Name(otherInstance), left);
            Assert.Contains(Name(notOurs), left);
            Assert.DoesNotContain(Name(finished), left);
            Assert.DoesNotContain(Name(orphan), left);
        }
    }

    [Fact]
    public async Task A_sweep_while_the_cluster_cannot_be_reached_lists_nothing_instead_of_failing_startup()
    {
        var unreachable = new KubernetesRunHost(
            new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = "http://127.0.0.1:1" }), Options(), FakeKubernetesApi.Namespace, new ProcessRunner(), _log);

        var leftovers = await unreachable.ListLeftoversAsync(new HashSet<Guid>(), TestContext.Current.CancellationToken);

        Assert.Empty(leftovers);
        Assert.Contains(_log.Warnings, w => w.Contains("Could not list"));
    }

    [Fact]
    public async Task The_startup_check_warns_when_the_storage_class_keeps_volumes_after_their_claim_is_gone()
    {
        _api.StorageClasses.Add(new V1StorageClass { Metadata = new V1ObjectMeta { Name = "run-scratch" }, Provisioner = "p", ReclaimPolicy = "Retain" });

        await _host.CheckStorageAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_log.Warnings, w => w.Contains("run-scratch") && w.Contains("Retain"));
    }

    [Fact]
    public async Task The_startup_check_is_quiet_for_a_class_that_deletes_and_for_the_default_class_when_none_is_set()
    {
        _api.StorageClasses.Add(new V1StorageClass { Metadata = new V1ObjectMeta { Name = "run-scratch" }, Provisioner = "p", ReclaimPolicy = "Delete" });
        await _host.CheckStorageAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_log.Warnings);

        _api.StorageClasses.Add(new V1StorageClass
        {
            Metadata = new V1ObjectMeta { Name = "std", Annotations = new Dictionary<string, string> { ["storageclass.kubernetes.io/is-default-class"] = "true" } },
            Provisioner = "p",
            ReclaimPolicy = "Retain",
        });
        var noClass = NewHost(Options() with { StorageClass = null });
        await noClass.CheckStorageAsync(TestContext.Current.CancellationToken);
        Assert.Contains(_log.Warnings, w => w.Contains("std"));
    }

    // -- Terminal, preview and routing ------------------------------------------

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
        Assert.Contains(exec.Resizes, r => r.Contains("120") && r.Contains("30"));
    }

    [Fact]
    public async Task The_preview_proxy_is_pointed_at_the_pods_address()
    {
        await CreateRunAsync();

        var target = await _host.PreviewTargetAsync(WorkingDirectory, 5173, TestContext.Current.CancellationToken);

        Assert.Equal("http://10.42.0.7:5173/", target.ToString());
    }

    [Fact]
    public void The_gateway_is_reached_at_ilds_service()
        => Assert.Equal("http://ild.example:8080", _host.GatewayApiUrl(null));

    [Fact]
    public async Task Files_are_read_and_written_by_commands_in_the_pod()
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

    private static FakeRunHost FakeLocal() => new();

    [Fact]
    public async Task Chats_and_other_paths_stay_on_the_local_host_and_loop_runs_go_to_the_pod()
    {
        await CreateRunAsync();
        _api.Execs.Clear();
        var local = FakeLocal().WithDirectory("/data/chat-sessions/c1");
        var routing = new RoutingRunHost(local, _host);
        var chat = Guid.NewGuid();

        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/c1", TestContext.Current.CancellationToken));
        Assert.Empty(_api.Execs);

        var chatCommand = RunCommand.Prepared(Command("/data/chat-sessions/c1", "git", "status"), chat);
        using var chatProcess = await routing.StartCommandAsync(chatCommand, TestContext.Current.CancellationToken);
        Assert.Same(chatCommand, Assert.Single(local.Started));
        Assert.Empty(_api.Execs);

        await routing.DirectoryExistsAsync(WorkingDirectory, TestContext.Current.CancellationToken);
        Assert.Single(_api.Execs);

        await routing.KillAllAsync(chat, TestContext.Current.CancellationToken);
        Assert.Equal([chat], local.KillAllCalls);
        Assert.Single(_api.Execs);
    }

    // -- Chats -------------------------------------------------------------------

    private static string ChatPodName => "ild-chat-main";

    private static string ChatFolder(Guid chat) => RunPaths.ChatRoot(chat);

    [Fact]
    public async Task A_chat_folder_and_its_commands_go_to_the_chat_pod_and_old_chats_stay_local()
    {
        var chat = Guid.NewGuid();
        var local = FakeLocal().WithDirectory("/data/chat-sessions/old");
        var routing = new RoutingRunHost(local, _host);

        var path = await routing.CreateChatFolderAsync(chat, "/data/chat-sessions", TestContext.Current.CancellationToken);
        using var process = await routing.StartCommandAsync(
            RunCommand.Prepared(Command(ChatFolder(chat), "git", "status"), chat), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(_host.HostsChats);
        Assert.Equal(ChatFolder(chat), path);
        Assert.Empty(local.ChatFolders);
        Assert.Empty(local.Started);
        Assert.All(_api.Execs, e => Assert.Equal(ChatPodName, e.Pod));
        Assert.Contains(RunPaths.ChatHome(chat), _api.Execs.First().Command);
        Assert.Contains(ChatFolder(chat), _api.Execs.Last().Command);

        var before = _api.Execs.Count;
        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/old", TestContext.Current.CancellationToken));
        Assert.Equal(before, _api.Execs.Count);
    }

    [Fact]
    public async Task The_chat_pod_claim_and_policy_are_made_once_for_every_chat_and_carry_no_run_label()
    {
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);

        Assert.Equal(1, _api.Created.Count(c => c == $"pods/{ChatPodName}"));
        Assert.Equal(1, _api.Created.Count(c => c == $"persistentvolumeclaims/{ChatPodName}"));
        Assert.Equal(1, _api.Created.Count(c => c == $"networkpolicies/{ChatPodName}"));

        var pod = _api.Get<V1Pod>("pods", ChatPodName)!;
        var claim = _api.Get<V1PersistentVolumeClaim>("persistentvolumeclaims", ChatPodName)!;
        var policy = _api.Get<V1NetworkPolicy>("networkpolicies", ChatPodName)!;
        foreach (var labels in new[] { pod.Metadata.Labels, claim.Metadata.Labels, policy.Metadata.Labels })
        {
            Assert.Equal("chat", labels[KubernetesNames.RoleLabel]);
            Assert.Equal(Instance, labels[KubernetesNames.InstanceLabel]);
            Assert.False(labels.ContainsKey(KubernetesNames.RunLabel));
        }
        Assert.Null(claim.Metadata.OwnerReferences);
        Assert.Equal("run-scratch", claim.Spec.StorageClassName);
        Assert.Equal(policy.Spec.PodSelector.MatchLabels, new Dictionary<string, string> { [KubernetesNames.RoleLabel] = "chat", [KubernetesNames.InstanceLabel] = Instance });
        Assert.Equal(["Ingress", "Egress"], policy.Spec.PolicyTypes);

        var container = pod.Spec.Containers.Single();
        Assert.Equal(RunPaths.ChatsRoot, container.WorkingDir);
        Assert.Equal(ChatPodName, pod.Spec.Volumes.Single(v => v.Name == "work").PersistentVolumeClaim.ClaimName);
        Assert.Equal(RunPaths.ChatsRoot, container.VolumeMounts.Single(v => v.Name == "work").MountPath);
        Assert.Null(container.Resources.Limits);
        Assert.NotEmpty(container.Resources.Requests);
        Assert.Contains(container.Env, e => e.Name == "GIT_AUTHOR_NAME" && e.Value == "Ada");
        Assert.DoesNotContain(container.VolumeMounts, v => v.Name == "tools");
    }

    [Fact]
    public async Task The_chat_pod_mounts_the_tools_claim_read_only_whenever_there_is_a_store()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));

        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);

        var pod = _api.Get<V1Pod>("pods", ChatPodName)!;
        Assert.True(pod.Spec.Volumes.Single(v => v.Name == "tools").PersistentVolumeClaim.ReadOnlyProperty);
        var mount = pod.Spec.Containers.Single().VolumeMounts.Single(v => v.Name == "tools");
        Assert.Equal(RunToolStore.Root, mount.MountPath);
        Assert.True(mount.ReadOnlyProperty);
    }

    [Fact]
    public async Task A_chat_pod_that_failed_is_replaced_and_its_claim_is_kept()
    {
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        _api.Get<V1Pod>("pods", ChatPodName)!.Status = new V1PodStatus { Phase = "Failed" };

        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);

        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{ChatPodName}"));
        Assert.Contains($"pods/{ChatPodName}", _api.Deleted);
        Assert.DoesNotContain($"persistentvolumeclaims/{ChatPodName}", _api.Deleted);
        Assert.Equal(1, _api.Created.Count(c => c == $"persistentvolumeclaims/{ChatPodName}"));
        Assert.Equal(ChatPodName, _api.Execs.Last().Pod);
    }

    [Fact]
    public async Task A_chat_pod_that_is_terminating_is_waited_out_before_a_new_one_is_made()
    {
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        _api.Get<V1Pod>("pods", ChatPodName)!.Metadata.DeletionTimestamp = DateTime.UtcNow;
        var client = _api.Client();
        var goes = Task.Run(async () =>
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            await client.CoreV1.DeleteNamespacedPodAsync(ChatPodName, FakeKubernetesApi.Namespace, cancellationToken: TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        await goes;

        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{ChatPodName}"));
        Assert.Null(_api.Get<V1Pod>("pods", ChatPodName)!.Metadata.DeletionTimestamp);
    }

    [Fact]
    public async Task Kill_all_for_a_chat_ends_what_runs_in_the_chat_pod_and_starts_no_pod_that_is_not_there()
    {
        var chat = Guid.NewGuid();
        Assert.Equal(0, await _host.KillAllAsync(chat, TestContext.Current.CancellationToken));
        Assert.Empty(_api.Execs);
        Assert.DoesNotContain(ChatPodName, _api.Names("pods"));

        await _host.CreateChatFolderAsync(chat, "/unused", TestContext.Current.CancellationToken);
        _api.OnExec = async e =>
        {
            await e.WriteStdoutAsync("1\n");
            await e.ExitAsync(0);
        };

        Assert.Equal(1, await _host.KillAllAsync(chat, TestContext.Current.CancellationToken));
        var kill = _api.Execs.Last();
        Assert.Equal(ChatPodName, kill.Pod);
        Assert.Equal(ExecRunHost.KillAllScript, kill.Command[2]);
    }

    [Fact]
    public async Task The_sweeper_leaves_the_chat_objects_alone()
    {
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);
        var sweeper = new RunHostSweeper(new RoutingRunHost(FakeLocal(), _host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));

        await sweeper.SweepAsync(_host, TestContext.Current.CancellationToken);

        Assert.Equal([ChatPodName], _api.Names("pods"));
        Assert.Equal([ChatPodName], _api.Names("persistentvolumeclaims"));
        Assert.Equal([ChatPodName], _api.Names("networkpolicies"));
    }

    [Fact]
    public async Task A_service_in_a_chat_cannot_be_previewed()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => _host.PreviewTargetAsync(RunPaths.ChatRoot(Guid.NewGuid()), 5173, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_toolchains_of_the_repositories_a_chat_clones_reach_its_commands()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));
        var chat = Guid.NewGuid();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Pod.StartsWith("ild-tools-", StringComparison.Ordinal))
                await e.WriteStdoutAsync("""
                    {"kind":"tool","spec":"node@20","version":"20.19.1","path":["/opt/ild-tools/mise/installs/node/20.19.1/bin"],"env":{}}

                    """);
            await e.ExitAsync(0);
        };

        await host.AddChatToolsAsync(ChatFolder(chat), MakeBaseRepository(("package.json", "{}"), (".nvmrc", "v20\n")), TestContext.Current.CancellationToken);
        Assert.Contains(_api.Created, c => c.StartsWith("pods/ild-tools-", StringComparison.Ordinal));

        using var process = await host.StartCommandAsync(RunCommand.Prepared(Command(ChatFolder(chat), "node", "-v"), chat), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        Assert.Equal(ChatPodName, exec.Pod);
        Assert.StartsWith("PATH=/opt/ild-tools/mise/installs/node/20.19.1/bin:", exec.Command.Single(c => c.StartsWith("PATH=", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_store_a_chat_gets_no_toolchains_and_no_installer()
    {
        var chat = Guid.NewGuid();

        await _host.AddChatToolsAsync(ChatFolder(chat), MakeBaseRepository(("package.json", "{}")), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_api.Created, c => c.StartsWith("pods/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_routing_host_creates_loop_runs_in_pods_and_leaves_the_leftovers_of_both_hosts_to_their_own()
    {
        var routing = new RoutingRunHost(FakeLocal(), _host);
        var env = await CreateRunThroughAsync(routing);
        SeedRun(Guid.NewGuid(), Instance);

        var leftovers = await routing.ListLeftoversAsync(new HashSet<Guid> { _run }, TestContext.Current.CancellationToken);

        Assert.Equal(WorkingDirectory, env.WorkingDirectory);
        var leftover = Assert.Single(leftovers, l => l.Kind == KubernetesRunHost.RunLeftoverKind);
        Assert.True(await routing.DeleteLeftoverAsync(leftover, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Name(Guid.ParseExact(leftover.Location, "N")), _api.Names("secrets"));
    }

    private async Task<RunEnvironment> CreateRunThroughAsync(IRunHost host)
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        return await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b"), TestContext.Current.CancellationToken);
    }

    // -- Settings ----------------------------------------------------------------

    [Theory]
    [InlineData("http://localhost:5000", "must be an address the run's pods can reach")]
    [InlineData("http://127.0.0.1:8080", "must be an address the run's pods can reach")]
    [InlineData(null, "ILD_API_URL must be ILD's Service address")]
    public void Settings_refuse_an_ild_address_a_pod_cannot_reach(string? apiUrl, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KubernetesRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => apiUrl,
            "ILD_K8S_RUNNER_IMAGE" => "img",
            _ => null,
        }));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Settings_require_the_runner_image()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => KubernetesRunHostOptions.From(name => name == "ILD_API_URL" ? "http://ild:8080" : null));

        Assert.Contains("ILD_K8S_RUNNER_IMAGE", ex.Message);
    }

    [Fact]
    public void Settings_default_to_low_requests_and_generous_limits_and_a_default_instance()
    {
        var options = KubernetesRunHostOptions.From(name => name switch { "ILD_API_URL" => "http://ild:8080", "ILD_K8S_RUNNER_IMAGE" => "img", _ => null });

        Assert.Equal("default", options.Instance);
        Assert.Null(options.StorageClass);
        Assert.Equal("100m", options.CpuRequest);
        Assert.Equal("4", options.CpuLimit);
        Assert.Equal(8080, options.IldPort);
    }

    [Fact]
    public void A_path_belongs_to_the_run_whose_directory_it_is_below()
    {
        Assert.Equal(_run, KubernetesNames.OwnerOf($"/ild-run/{_run:N}/repo/src/a.cs"));
        Assert.Equal(_run, KubernetesNames.OwnerOf($"/ild-run/{_run:N}"));
        Assert.Null(KubernetesNames.OwnerOf("/ild-run/not-a-run/repo"));
        Assert.Null(KubernetesNames.OwnerOf("/worktrees/ild/repo"));
        Assert.Null(KubernetesNames.OwnerOf(null));
    }
}
