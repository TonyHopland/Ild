using ILD.Core.Services.RunHosts.Local;
using System.Diagnostics;
using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Network;
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

[Collection(ProcessGlobalStateCollection.Name)]
public sealed class KubernetesRunHostTests : IAsyncLifetime
{
    private const string Instance = "main";

    private readonly string? _previousProxyPort = Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar);
    private readonly Guid _run = Guid.NewGuid();
    private static readonly Guid Repo = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherRepo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"ild-k8s-{Guid.NewGuid():N}");
    private readonly RecordingLogger<KubernetesRunHost> _log = new();
    private FakeKubernetesApi _api = null!;
    private KubernetesRunHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, "3128");
        Directory.CreateDirectory(_scratch);
        _api = await FakeKubernetesApi.StartAsync();
        _host = NewHost(Options());
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, _previousProxyPort);
        if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
    }

    private static KubernetesRunHostOptions Options(string instance = Instance, string? toolsClaim = null, string image = "registry.example/ild-runner:1") => KubernetesRunHostOptions.From(name => name switch
    {
        "ILD_API_URL" => "http://ild.example:8080",
        "ILD_K8S_RUNNER_IMAGE" => image,
        "ILD_K8S_INSTANCE" => instance,
        "ILD_K8S_STORAGE_CLASS" => "run-scratch",
        "ILD_K8S_ILD_NAMESPACE" => "ild-system",
        "ILD_K8S_TOOLS_CLAIM" => toolsClaim,
        "ILD_NETWORK_PROXY_PORT" => "3128",
        "GIT_AUTHOR_NAME" => "Ada",
        _ => null,
    }) with { ReadyTimeout = TimeSpan.FromSeconds(10) };

    private KubernetesRunHost NewHost(KubernetesRunHostOptions options, IReadOnlyList<RunAgent>? agents = null, TimeProvider? time = null)
        => new(_api.Client(), options, FakeKubernetesApi.Namespace, new ProcessRunner(), _log) { Credentials = EgressProxyTests.Creds, Time = time ?? TimeProvider.System, ReadyPollInterval = TimeSpan.FromMilliseconds(10), Agents = () => agents ?? [], ChatToolsRoot = () => Path.Combine(_scratch, "chat-tools"), ResolveIld = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("10.96.0.20") }), ClusterNameServer = () => "10.96.0.10" };

    private string WorkingDirectory => RunPaths.WorkingDirectory(_run);

    private static string Name(Guid run) => KubernetesNames.ObjectName(run);

    private string MakeBaseRepository(params (string Path, string Content)[] files) => MakeBaseRepositoryIn("base", files);

    private string MakeBaseRepositoryIn(string name, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_scratch, name);
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
        return await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);
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
        Assert.Equal([$"configmaps/{Name(_run)}", $"persistentvolumeclaims/{Name(_run)}", $"networkpolicies/{Name(_run)}", $"pods/{Name(_run)}"], _api.Created);

        var secret = _api.Get<V1ConfigMap>("configmaps", Name(_run))!;
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
            Assert.Equal("ConfigMap", owner.Kind);
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

        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

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
    public async Task Create_gives_the_pod_the_clusters_nameserver_the_policy_shuts_and_ilds_service_address_for_ilds_name()
    {
        await CreateRunAsync();

        var spec = _api.Get<V1Pod>("pods", Name(_run))!.Spec;
        Assert.Equal("None", spec.DnsPolicy);
        Assert.Equal(["10.96.0.10"], spec.DnsConfig.Nameservers);
        var alias = Assert.Single(spec.HostAliases);
        Assert.Equal("10.96.0.20", alias.Ip);
        Assert.Equal(["ild.example"], alias.Hostnames);
    }

    [Fact]
    public async Task A_run_that_asks_for_dns_keeps_the_clusters_resolver_and_may_reach_it()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo, Dns: true), TestContext.Current.CancellationToken);

        var spec = _api.Get<V1Pod>("pods", Name(_run))!.Spec;
        Assert.Null(spec.DnsPolicy);
        Assert.Null(spec.HostAliases);
        var policy = _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Spec;
        Assert.Equal([53, 53], policy.Egress[1].Ports.Select(p => int.Parse(p.Port.Value)).ToArray());
    }

    [Fact]
    public async Task Create_lets_the_pod_reach_only_ild_and_be_reached_only_by_ild()
    {
        await CreateRunAsync();

        var policy = _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Spec;
        Assert.Equal(["Ingress", "Egress"], policy.PolicyTypes);
        Assert.Equal(_run.ToString("N"), policy.PodSelector.MatchLabels["ild/run"]);
        var toIld = policy.Egress[0];
        Assert.Equal("ild", toIld.To.Single().PodSelector.MatchLabels["app.kubernetes.io/name"]);
        Assert.Equal([8080, 3128, 8442], toIld.Ports.Select(p => int.Parse(p.Port.Value)).ToArray());
        Assert.Single(policy.Egress);
        Assert.Equal("ild", policy.Ingress.Single().FromProperty.Single().PodSelector.MatchLabels["app.kubernetes.io/name"]);
    }

    [Fact]
    public async Task Create_takes_a_pod_that_is_already_healthy_as_it_is()
    {
        await CreateRunAsync();
        _api.Created.Clear();
        _api.Execs.Clear();

        var again = await _host.CreateAsync(new RunEnvironmentRequest(_run, "/unused", "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

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
            () => _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b", Repo), TestContext.Current.CancellationToken));

        Assert.Contains("fatal: bad bundle", ex.Message);
        Assert.Empty(_api.Names("pods"));
        Assert.Empty(_api.Names("configmaps"));
    }

    // -- Shutdown and the chat pod's spec -----------------------------------------

    [Fact]
    public async Task Shutdown_deletes_the_chat_pod_and_open_sign_in_pods_and_leaves_run_pods()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && !e.Tty) await e.ReadStdinToEndAsync();
            if (!e.Tty) await e.ExitAsync(0);
        };
        var ct = TestContext.Current.CancellationToken;
        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), ct);
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        await using var signIn = await _host.OpenSignInAsync(null, "claude", [], 80, 24, ct);
        var signInPod = SignInPod();

        await _host.ShutdownAsync(ct);

        Assert.Contains($"pods/{KubernetesNames.ChatName(Instance)}", _api.Deleted);
        Assert.Contains($"pods/{signInPod}", _api.Deleted);
        Assert.DoesNotContain($"pods/{Name(_run)}", _api.Deleted);
    }

    [Fact]
    public async Task A_chat_pod_made_from_another_spec_is_replaced_and_one_from_the_same_is_kept()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        var ct = TestContext.Current.CancellationToken;
        await _host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        var chat = KubernetesNames.ChatName(Instance);

        await NewHost(Options()).CheckAsync(ct);
        Assert.DoesNotContain($"pods/{chat}", _api.Deleted);

        // A new runner image.
        var upgraded = NewHost(Options(image: "registry.example/ild-runner:2"));
        await upgraded.CheckAsync(ct);
        Assert.Contains($"pods/{chat}", _api.Deleted);

        await upgraded.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        Assert.Equal("registry.example/ild-runner:2", _api.Get<V1Pod>("pods", chat)!.Spec.Containers.Single().Image);
    }

    // -- Provider sign-in --------------------------------------------------------

    private string SignInPod() => Assert.Single(_api.Names("pods"), n => n.StartsWith("ild-signin-", StringComparison.Ordinal));

    [Fact]
    public async Task The_sign_in_terminal_runs_in_a_pod_of_its_own_and_reads_the_login_from_its_home()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && !e.Tty) await e.ReadStdinToEndAsync();
            if (e.Joined.Contains("base64", StringComparison.Ordinal))
                await e.WriteStdoutAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"token\":\"x\"}")));
            if (!e.Tty) await e.ExitAsync(0);
        };
        var ct = TestContext.Current.CancellationToken;

        var terminal = await _host.OpenSignInAsync(ManagedAgentCatalog.ClaudeCode, "/usr/local/bin/claude", ["setup-token"], 100, 30, ct);
        var name = SignInPod();
        var pod = _api.Get<V1Pod>("pods", name)!;
        Assert.Equal(KubernetesNames.SignInRole, pod.Metadata.Labels[RunPaths.RoleLabel]);
        // No run label: no run's NetworkPolicy selects it, so the OAuth flow can reach the provider.
        Assert.False(pod.Metadata.Labels.ContainsKey(RunPaths.RunLabel));
        Assert.False(pod.Spec.AutomountServiceAccountToken);
        Assert.Equal("/tmp/home", pod.Spec.Containers.Single().Env.Single(e => e.Name == "HOME").Value);
        Assert.Empty(pod.Spec.Volumes ?? []);
        var shell = Assert.Single(_api.Execs, e => e.Pod == name && e.Tty);
        Assert.Equal(["claude", "setup-token"], shell.Command.TakeLast(2));

        Assert.Equal("{\"token\":\"x\"}", await terminal.ReadLoginAsync(".claude/.credentials.json", ct));

        await terminal.DisposeAsync();
        Assert.Contains($"pods/{name}", _api.Deleted);
    }

    [Fact]
    public async Task A_sign_in_pod_left_behind_is_swept_and_one_still_open_is_not()
    {
        _api.OnExec = e => e.Tty ? Task.CompletedTask : e.ExitAsync(0);
        var ct = TestContext.Current.CancellationToken;
        await using var open = await _host.OpenSignInAsync(null, "claude", [], 80, 24, ct);
        var name = SignInPod();

        Assert.DoesNotContain(await _host.ListLeftoversAsync(new HashSet<Guid>(), ct), l => l.Location == name);

        // Another ILD, or this one after a restart, has no terminal open in it.
        var restarted = NewHost(Options());
        var leftover = Assert.Single(await restarted.ListLeftoversAsync(new HashSet<Guid>(), ct), l => l.Location == name);
        Assert.True(await restarted.DeleteLeftoverAsync(leftover, ct));
        Assert.Contains($"pods/{name}", _api.Deleted);
    }

    // -- Toolchains --------------------------------------------------------------

    private const string AgentNodeLine = """{"kind":"agentnode","spec":"node@24","version":"24.11.0","path":["/opt/ild-tools/mise/installs/node/24.11.0/bin"],"env":{}}""";
    private const string PiLine = """{"kind":"agent","key":"pi","version":"1.5.0","path":["/opt/ild-tools/agents/pi/1.5.0/wrap"]}""";
    private const string NodeLine = """{"kind":"tool","spec":"node@24","version":"24.11.0","path":["/opt/ild-toolchains/mise/installs/node/24.11.0/bin"],"env":{}}""";

    // Answers each installer pod with the lines of the real script's output that are in the zone it installs into (its
    // ILD_TOOLS_ROOT), and records the pod it ran in with the command.
    private List<(string Pod, IReadOnlyList<string> Command)> AnswerInstallers(params string[] lines)
    {
        var installs = new List<(string Pod, IReadOnlyList<string> Command)>();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Pod.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                lock (installs) installs.Add((e.Pod, e.Command));
                _installerPods[e.Pod] = _api.Get<V1Pod>("pods", e.Pod)!;
                var root = EnvOf(_api.Get<V1Pod>("pods", e.Pod)!, "ILD_TOOLS_ROOT");
                await e.WriteStdoutAsync(string.Join('\n', lines.Where(l => l.Contains($"\"{root}/", StringComparison.Ordinal))) + "\n");
            }
            await e.ExitAsync(0);
        };
        return installs;
    }

    // The installer pods as they were while they ran: they are deleted when the install ends.
    private readonly Dictionary<string, V1Pod> _installerPods = [];

    private V1Pod Installer(string pod) => _installerPods[pod];

    private static string EnvOf(V1Pod pod, string name) => pod.Spec.Containers.Single().Env.Single(e => e.Name == name).Value;

    private static string RepoZonePath(string folder) => $"{folder}/mise/installs/npm-prettier/3.3.0/bin";

    private static string PrettierLine(string folder)
        => $$$"""{"kind":"tool","spec":"npm:prettier@3","version":"3.3.0","path":["{{{RepoZonePath(folder)}}}"],"env":{}}""";

    private static readonly string PrettierFolder = RunToolStore.RepositoryFolder([new RunTool("npm:prettier", "3")], false);

    [Fact]
    public async Task An_installer_pod_reaches_only_ilds_proxy_through_a_policy_made_first_and_removed_with_it()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);
        AnswerInstallers(AgentNodeLine, PiLine);
        var inner = _api.OnExec!;
        V1NetworkPolicy? policy = null;
        _api.OnExec = e =>
        {
            if (e.Pod.StartsWith("ild-tools-", StringComparison.Ordinal)) policy = _api.Get<V1NetworkPolicy>("networkpolicies", e.Pod);
            return inner(e);
        };

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        var pod = Assert.Single(_installerPods.Values);
        Assert.NotNull(policy);
        Assert.Equal(["Ingress", "Egress"], policy.Spec.PolicyTypes);
        Assert.Equal(pod.Metadata.Labels["ild/installer"], policy.Spec.PodSelector.MatchLabels["ild/installer"]);
        var toIld = Assert.Single(policy.Spec.Egress);
        Assert.Equal("ild", toIld.To.Single().PodSelector.MatchLabels["app.kubernetes.io/name"]);
        Assert.Equal([3128], toIld.Ports.Select(p => int.Parse(p.Port.Value)).ToArray());
        Assert.Empty(policy.Spec.Ingress);

        // Names are resolved only by the proxy: ILD's own name is pinned in the pod, and the nameserver is nothing.
        Assert.Equal("None", pod.Spec.DnsPolicy);
        Assert.Equal(["127.0.0.1"], pod.Spec.DnsConfig.Nameservers);
        Assert.Equal("10.96.0.20", Assert.Single(pod.Spec.HostAliases).Ip);
        Assert.EndsWith("@ild.example:3128", EnvOf(pod, "HTTPS_PROXY"));
        Assert.True(AgentIsolationEgressTests.TryReadUrl(EgressProxyTests.Creds, EnvOf(pod, "HTTPS_PROXY"), out var scope));
        Assert.Equal(new LaunchScope(LaunchKind.Installer, Installer: "agents"), scope);
        Assert.Equal("1", EnvOf(pod, "NODE_USE_ENV_PROXY"));

        // Created before the pod, and gone with it.
        Assert.True(_api.Created.IndexOf($"networkpolicies/{pod.Metadata.Name}") < _api.Created.IndexOf($"pods/{pod.Metadata.Name}"));
        Assert.Contains($"networkpolicies/{pod.Metadata.Name}", _api.Deleted);
        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", pod.Metadata.Name));
    }

    [Fact]
    public async Task With_a_tools_claim_each_zone_of_the_store_is_filled_by_an_installer_pod_that_mounts_only_it_and_mounted_read_only_in_the_run()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);
        var installs = AnswerInstallers(AgentNodeLine, PiLine, NodeLine);

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(("package.json", "{}")), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        Assert.Equal(2, installs.Count);
        var agents = installs.Single(i => i.Command.Contains("agentnode=node@24"));
        Assert.Equal(["agentnode=node@24", "agent=pi|@earendil-works/pi-coding-agent|1.5.0|pi"], agents.Command.TakeLast(2));
        var toolchains = installs.Single(i => i.Command.Contains("tool=node@24"));
        Assert.DoesNotContain("agentnode=node@24", toolchains.Command);
        foreach (var (pod, _) in installs) Assert.Contains($"pods/{pod}", _api.Deleted);

        var agentInstaller = Installer(agents.Pod);
        Assert.Equal("ild-tools", agentInstaller.Spec.Volumes.Single().PersistentVolumeClaim.ClaimName);
        Assert.Null(agentInstaller.Spec.Volumes.Single().PersistentVolumeClaim.ReadOnlyProperty);
        Assert.False(agentInstaller.Metadata.Labels.ContainsKey(RunPaths.RunLabel));
        var agentMount = agentInstaller.Spec.Containers.Single().VolumeMounts.Single();
        Assert.Equal((RunToolStore.Root, "ild", false), (agentMount.MountPath, agentMount.SubPath, agentMount.ReadOnlyProperty));
        var toolchainMount = Installer(toolchains.Pod).Spec.Containers.Single().VolumeMounts.Single();
        Assert.Equal((RunToolStore.ToolchainsRoot, "toolchains"), (toolchainMount.MountPath, toolchainMount.SubPath));

        var run = _api.Get<V1Pod>("pods", Name(_run))!;
        var claim = run.Spec.Volumes.Single(v => v.PersistentVolumeClaim?.ClaimName == "ild-tools");
        Assert.True(claim.PersistentVolumeClaim.ReadOnlyProperty);
        var mounts = run.Spec.Containers.Single().VolumeMounts.Where(m => m.Name == claim.Name).ToList();
        Assert.Equal([(RunToolStore.ToolchainsRoot, "toolchains"), (RunToolStore.Root, "ild")], mounts.Select(m => (m.MountPath, m.SubPath)).OrderBy(m => m.MountPath));
        Assert.All(mounts, m => Assert.True(m.ReadOnlyProperty));
        var path = run.Spec.Containers.Single().Env.Single(e => e.Name == "PATH").Value;
        Assert.StartsWith("/opt/ild-toolchains/mise/installs/node/24.11.0/bin:/opt/ild-tools/agents/pi/1.5.0/wrap:", path);
    }

    [Fact]
    public async Task A_repositorys_own_tools_are_installed_into_its_own_folder_by_a_pod_that_cannot_see_the_agents_or_another_repository()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);
        var installs = AnswerInstallers(AgentNodeLine, PiLine, NodeLine, PrettierLine(PrettierFolder));
        var repo = MakeBaseRepository(("package.json", "{}"), ("mise.toml", "[tools]\n\"npm:prettier\" = \"3\"\n"));

        await host.CreateAsync(new RunEnvironmentRequest(_run, repo, "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        var installer = Installer(installs.Single(i => i.Command.Contains("tool=npm:prettier@3")).Pod);
        Assert.Equal(PrettierFolder, EnvOf(installer, "ILD_TOOLS_ROOT"));
        Assert.DoesNotContain(installer.Spec.Containers.Single().Env, e => e.Name == "npm_config_ignore_scripts");
        var mounts = installer.Spec.Containers.Single().VolumeMounts;
        Assert.Equal(
            [(RunToolStore.RepositoryToolsRoot, $"repo-tools/{Repo:N}", (bool?)false), (RunToolStore.ToolchainsRoot, "toolchains", true)],
            mounts.Select(m => (m.MountPath, m.SubPath, m.ReadOnlyProperty)).OrderBy(m => m.MountPath));
        Assert.StartsWith("/opt/ild-toolchains/mise/installs/node/24.11.0/bin:", EnvOf(installer, "PATH"), StringComparison.Ordinal);
        Assert.DoesNotContain(mounts, m => m.MountPath == RunToolStore.Root);

        var run = _api.Get<V1Pod>("pods", Name(_run))!;
        Assert.Contains(run.Spec.Containers.Single().VolumeMounts, m => m.SubPath == $"repo-tools/{Repo:N}" && m.ReadOnlyProperty == true);
        Assert.StartsWith(RepoZonePath(PrettierFolder), run.Spec.Containers.Single().Env.Single(e => e.Name == "PATH").Value.Split(':').Single(p => p.StartsWith(RunToolStore.RepositoryToolsRoot, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Another_repositorys_run_does_not_mount_the_first_repositorys_folder()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));
        AnswerInstallers(NodeLine, PrettierLine(PrettierFolder));
        var repo = MakeBaseRepository(("package.json", "{}"), ("mise.toml", "[tools]\n\"npm:prettier\" = \"3\"\n"));
        var other = MakeBaseRepositoryIn("other", ("package.json", "{}"));
        var otherRun = Guid.NewGuid();

        await host.CreateAsync(new RunEnvironmentRequest(_run, repo, "b", Repo), TestContext.Current.CancellationToken);
        await host.CreateAsync(new RunEnvironmentRequest(otherRun, other, "b", OtherRepo), TestContext.Current.CancellationToken);

        var mounts = _api.Get<V1Pod>("pods", Name(otherRun))!.Spec.Containers.Single().VolumeMounts;
        Assert.DoesNotContain(mounts, m => m.MountPath == RunToolStore.RepositoryToolsRoot);
        Assert.DoesNotContain(mounts, m => m.SubPath?.StartsWith("repo-tools", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task The_installer_pod_makes_its_zones_folder_first_with_a_fixed_command_and_never_mounts_the_claims_root_in_the_container_that_installs()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));
        var installs = AnswerInstallers(NodeLine);

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(("package.json", "{}")), "b", Repo), TestContext.Current.CancellationToken);

        var spec = Installer(Assert.Single(installs).Pod).Spec;
        var init = Assert.Single(spec.InitContainers);
        Assert.Equal(["mkdir", "-p", "/claim/toolchains"], init.Command);
        Assert.All(spec.Containers.Single().VolumeMounts, m => Assert.NotNull(m.SubPath));
    }

    [Fact]
    public async Task Without_a_tools_claim_a_run_has_only_its_image_and_the_startup_check_says_so()
    {
        var host = NewHost(Options(), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(("package.json", "{}")), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);
        await host.CheckAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_api.Created, c => c.StartsWith("pods/ild-tools-", StringComparison.Ordinal));
        var run = _api.Get<V1Pod>("pods", Name(_run))!;
        Assert.DoesNotContain(run.Spec.Containers.Single().Env, e => e.Name == "PATH");
        Assert.Contains(_log.Warnings, w => w.Contains("ILD_K8S_TOOLS_CLAIM"));
    }

    // -- Forwards ----------------------------------------------------------------

    [Fact]
    public async Task A_pod_whose_hosts_file_cannot_be_written_keeps_the_loopback_form_and_says_so_once()
    {
        await CreateRunAsync();
        _api.OnExec = e => e.ExitAsync(e.Command.Contains(ExecRunHost.HostsScript) ? 3 : 0);
        _host.Forwards = _ => Task.FromResult<IReadOnlyList<RunForward>>([new(15432, "postgres", 5432)]);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 2; i++)
            using (var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run), ct))
                await process.WaitForExitAsync(ct);

        // The hosts script runs as the pod's own user (the exec has no other), and the relay gets no address.
        var syncs = _api.Execs.Where(e => e.Command.Contains(ExecRunHost.ForwardsScript)).ToList();
        Assert.Equal(2, syncs.Count);
        Assert.All(syncs, sync => Assert.Equal(["15432", "postgres", "5432", "-"], sync.Command.TakeLast(4)));
        Assert.Single(_log.Warnings, w => w.Contains("hosts file"));
    }

    [Fact]
    public async Task ILDs_own_scripts_start_with_a_fixed_environment_and_the_runs_own_command_does_not()
    {
        await CreateRunAsync();
        _host.Forwards = _ => Task.FromResult<IReadOnlyList<RunForward>>([new(15432, "postgres", 5432)]);
        var ct = TestContext.Current.CancellationToken;

        using (var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "make-it"), _run), ct)) await process.WaitForExitAsync(ct);
        await _host.GetFileLengthAsync($"{WorkingDirectory}/file", ct);

        var own = _api.Execs.Where(e => e.Command.Contains(ExecRunHost.ForwardsScript) || e.Command.Contains(ExecRunHost.HostsScript) || e.Joined.Contains("stat -c %s")).ToList();
        Assert.Equal(3, own.Count);
        Assert.All(own, e => Assert.Equal(["/usr/bin/env", "-i", "PATH=/usr/sbin:/usr/bin:/sbin:/bin"], e.Command.Take(3)));
        Assert.NotEqual("/usr/bin/env", _api.Execs.Single(e => e.Command.Contains("make-it")).Command[0]);
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
        Assert.Equal(Name(_run), exec.Pod);
        Assert.Contains("ILD_TEST_ONLY=yes", Env(exec));
        Assert.Equal($"HOME={RunPaths.HomeDirectory(_run)}", Env(exec).Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.DoesNotContain(Env(exec), c => c.StartsWith("PATH=", StringComparison.Ordinal));
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
        Assert.Contains("connection to the run's pod", await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_launch_runs_the_cli_by_name_as_the_pod_has_it_and_without_the_local_uid_crossing()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "/data/agents/claude-code/v1/node_modules/.bin/claude", "-p");

        using var process = await _host.StartCommandAsync(
            RunCommand.Agent(psi, _run, null, NetworkMode.Off, new Dictionary<string, string?>
            {
                ["FROM_CONTEXT"] = "1", ["OPENAI_BASE_URL"] = "http://ild.example:8080/gateway",
            }), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        Assert.DoesNotContain("/usr/bin/setpriv", exec.Command);
        Assert.Equal(["claude", "-p"], exec.Command.TakeLast(2));
        Assert.Contains("FROM_CONTEXT=1", Env(exec));
        Assert.Contains("OPENAI_BASE_URL=http://ild.example:8080/gateway", Env(exec));
    }

    // -- The direct route (network mode Off) -------------------------------------

    private string OpenPolicyName => KubernetesNames.OpenName(Name(_run));

    private async Task<IRunProcess> StartAsync(NetworkMode mode, bool prepared = false, KubernetesRunHost? host = null)
    {
        var process = await LaunchAsync(mode, prepared, host);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process;
    }

    // A launch that is not waited for, so one that is held keeps running.
    private async Task<IRunProcess> LaunchAsync(NetworkMode mode, bool prepared = false, KubernetesRunHost? host = null)
    {
        var psi = Command(WorkingDirectory, "ssh", "-T", "git@github.com");
        var command = prepared ? RunCommand.Prepared(psi, _run, networkMode: mode) : RunCommand.Agent(psi, _run, Guid.NewGuid(), mode, null);
        return await (host ?? _host).StartCommandAsync(command, TestContext.Current.CancellationToken);
    }

    private static bool IsOffLaunch(FakeExec e) => e.Joined.Contains($"/{ExecRunHost.OffPrefix}", StringComparison.Ordinal) && e.Joined.Contains("setsid -w", StringComparison.Ordinal);

    // The exec of every launch in mode Off stays running until released; anything else ends at once.
    private TaskCompletionSource HoldOffLaunches()
    {
        var release = new TaskCompletionSource();
        _api.OnExec = async e =>
        {
            if (IsOffLaunch(e)) await release.Task;
            await e.ExitAsync(0);
        };
        return release;
    }

    // As if the pod always said a process group in mode Off were alive, which is all a run can make it say.
    private void PlantAnOffFile()
        => _api.OnExec = e => e.ExitAsync(e.Joined.Contains("setsid -w", StringComparison.Ordinal) ? 0 : 1);

    private async Task<IRunProcess> ChatLaunchAsync(Guid chat, NetworkMode mode)
        => await _host.StartCommandAsync(RunCommand.Prepared(Command(ChatFolder(chat), "git", "status"), chat, networkMode: mode), TestContext.Current.CancellationToken);

    private string ChatOpenPolicyName => KubernetesNames.OpenName(ChatPodName);

    private static readonly string[] ProxyVariables = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "NODE_USE_ENV_PROXY", "NO_PROXY", "no_proxy"];

    // The exec of a started command: the one that runs the wrapper. Its environment is in the file the wrapper sources.
    private FakeExec LaunchExec() => _api.Execs.Last(e => e.Command.Any(c => c.Contains("setsid -w", StringComparison.Ordinal)));

    private IReadOnlyList<string> Env(FakeExec exec) => _api.EnvironmentOf(exec);

    private IEnumerable<string> LaunchProxyVariables()
        => Env(LaunchExec()).Where(c => ProxyVariables.Any(v => c.StartsWith(v + "=", StringComparison.Ordinal)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_launch_in_off_makes_an_allow_all_egress_policy_owned_by_the_runs_secret_and_gets_no_proxy_variables(bool prepared)
    {
        await CreateRunAsync();
        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName));

        using var process = await StartAsync(NetworkMode.Off, prepared);

        var policy = _api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName)!;
        Assert.Equal(["Egress"], policy.Spec.PolicyTypes);
        Assert.Empty(Assert.Single(policy.Spec.Egress).To ?? []);
        Assert.Empty(Assert.Single(policy.Spec.Egress).Ports ?? []);
        Assert.Equal(_run.ToString("N"), policy.Spec.PodSelector.MatchLabels["ild/run"]);
        Assert.Equal("ild", policy.Metadata.Labels["app.kubernetes.io/managed-by"]);
        Assert.Equal(Instance, policy.Metadata.Labels["ild/instance"]);
        Assert.Equal(_run.ToString("N"), policy.Metadata.Labels["ild/run"]);
        Assert.Equal("open", policy.Metadata.Labels["ild/role"]);
        var owner = Assert.Single(policy.Metadata.OwnerReferences);
        Assert.Equal("ConfigMap", owner.Kind);
        Assert.Equal(Name(_run), owner.Name);
        Assert.Empty(LaunchProxyVariables());
        // The run's own policy is untouched: the route is another policy on top of it.
        Assert.Equal(["Ingress", "Egress"], _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Spec.PolicyTypes);
    }

    [Theory]
    [InlineData(NetworkMode.Monitor)]
    [InlineData(NetworkMode.Whitelist)]
    public async Task Returning_to_a_proxied_mode_with_no_off_process_left_deletes_the_open_policy(NetworkMode mode)
    {
        await CreateRunAsync();
        using (await StartAsync(NetworkMode.Off)) { }
        Assert.NotNull(_api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName));

        using var process = await StartAsync(mode);

        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName));
        Assert.Contains($"networkpolicies/{OpenPolicyName}", _api.Deleted);
        Assert.NotEmpty(LaunchProxyVariables());
    }

    [Fact]
    public async Task Returning_to_a_proxied_mode_with_an_off_process_still_running_keeps_the_open_policy()
    {
        await CreateRunAsync();
        var release = HoldOffLaunches();
        using var off = await LaunchAsync(NetworkMode.Off);

        using var process = await StartAsync(NetworkMode.Monitor);

        Assert.NotNull(_api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName));
        release.SetResult();
        await off.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_planted_off_file_does_not_keep_the_open_policy_once_ilds_own_off_launches_have_ended()
    {
        await CreateRunAsync();
        using (await StartAsync(NetworkMode.Off)) { }
        PlantAnOffFile();

        using var process = await StartAsync(NetworkMode.Monitor);

        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName));
        Assert.NotEmpty(LaunchProxyVariables());
    }

    [Fact]
    public async Task A_restarted_ild_closes_an_open_policy_it_finds_on_its_first_proxied_launch()
    {
        await CreateRunAsync();
        var release = HoldOffLaunches();
        using var off = await LaunchAsync(NetworkMode.Off);
        _api.OnExec = e => e.ExitAsync(0);
        var restarted = NewHost(Options());

        using var process = await StartAsync(NetworkMode.Monitor, host: restarted);

        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName));
        release.SetResult();
    }

    [Fact]
    public async Task A_planted_off_file_does_not_keep_the_chat_pods_open_policy_once_no_off_chat_launch_is_live()
    {
        var ended = Guid.NewGuid();
        var other = Guid.NewGuid();
        await _host.CreateChatFolderAsync(ended, "/unused", TestContext.Current.CancellationToken);
        await _host.CreateChatFolderAsync(other, "/unused", TestContext.Current.CancellationToken);
        using (var off = await ChatLaunchAsync(ended, NetworkMode.Off)) await off.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(_api.Get<V1NetworkPolicy>("networkpolicies", ChatOpenPolicyName));
        PlantAnOffFile();

        using var process = await ChatLaunchAsync(other, NetworkMode.Monitor);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", ChatOpenPolicyName));
    }

    [Fact]
    public async Task A_live_off_launch_of_one_chat_keeps_the_chat_pods_open_policy_for_a_proxied_launch_of_another()
    {
        var live = Guid.NewGuid();
        var other = Guid.NewGuid();
        await _host.CreateChatFolderAsync(live, "/unused", TestContext.Current.CancellationToken);
        await _host.CreateChatFolderAsync(other, "/unused", TestContext.Current.CancellationToken);
        var release = HoldOffLaunches();
        using var off = await ChatLaunchAsync(live, NetworkMode.Off);

        using var process = await ChatLaunchAsync(other, NetworkMode.Monitor);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(_api.Get<V1NetworkPolicy>("networkpolicies", ChatOpenPolicyName));
        release.SetResult();
    }

    [Fact]
    public async Task Two_pods_in_off_cannot_reach_each_other_because_a_pods_policy_takes_ingress_from_ilds_pod_only_and_the_open_policy_adds_none()
    {
        await CreateRunAsync();
        using var process = await StartAsync(NetworkMode.Off);

        foreach (var name in new[] { Name(_run), OpenPolicyName })
        {
            var spec = _api.Get<V1NetworkPolicy>("networkpolicies", name)!.Spec;
            if (name == OpenPolicyName)
            {
                Assert.DoesNotContain("Ingress", spec.PolicyTypes);
                Assert.Empty(spec.Ingress ?? []);
                continue;
            }
            Assert.Contains("Ingress", spec.PolicyTypes);
            var from = Assert.Single(Assert.Single(spec.Ingress).FromProperty);
            Assert.Equal(Options().IldPodSelector, from.PodSelector.MatchLabels);
            Assert.Equal(new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = "ild-system" }, from.NamespaceSelector.MatchLabels);
            Assert.Null(from.IpBlock);
        }
    }

    [Fact]
    public async Task The_runs_that_have_a_direct_route_are_the_ones_with_an_open_policy()
    {
        await CreateRunAsync();
        Assert.Empty(await _host.DirectRoutesAsync(TestContext.Current.CancellationToken));

        using var process = await StartAsync(NetworkMode.Off);

        Assert.Equal([new DirectRoute(_run, Chat: false)], await _host.DirectRoutesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_sweep_removes_an_open_policy_a_dead_run_left_and_deleting_the_run_removes_it_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var dead = Guid.NewGuid();
        _api.Seed("networkpolicies", KubernetesNames.OpenName(Name(dead)), Instance, dead);

        var leftover = Assert.Single(await _host.ListLeftoversAsync(new HashSet<Guid>(), ct));
        Assert.Equal(dead.ToString("N"), leftover.Location);
        Assert.True(await _host.DeleteLeftoverAsync(leftover, ct));

        Assert.Contains($"networkpolicies/{KubernetesNames.OpenName(Name(dead))}", _api.Deleted);
        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", KubernetesNames.OpenName(Name(dead))));
    }

    [Fact]
    public async Task Ending_the_run_takes_the_open_policy_with_its_secret()
    {
        var env = await CreateRunAsync();
        using (await StartAsync(NetworkMode.Off)) { }

        await _host.DeleteAsync(env, TestContext.Current.CancellationToken);

        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName));
    }

    [Fact]
    public async Task The_sweep_removes_the_chat_pods_open_policy_when_the_pod_is_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = KubernetesNames.OpenName(KubernetesNames.ChatName(Instance));
        _api.Seed("networkpolicies", name, Instance, Guid.Empty);
        // Not a run's: it carries no run label.
        _api.Get<V1NetworkPolicy>("networkpolicies", name)!.Metadata.Labels.Remove("ild/run");
        _api.Get<V1NetworkPolicy>("networkpolicies", name)!.Metadata.Labels["ild/role"] = "open";

        var leftover = Assert.Single(await _host.ListLeftoversAsync(new HashSet<Guid>(), ct), l => l.Location == name);
        Assert.True(await _host.DeleteLeftoverAsync(leftover, ct));

        Assert.Null(_api.Get<V1NetworkPolicy>("networkpolicies", name));
    }

    [Fact]
    public async Task Proxy_variables_that_name_ilds_loopback_name_ilds_address_for_the_pod()
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "curl");

        using var process = await _host.StartCommandAsync(
            RunCommand.Prepared(psi, _run, new Dictionary<string, string?> { ["HTTP_PROXY"] = "http://scope:abc@127.0.0.1:3128" }), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var proxy = Assert.Single(Env(_api.Execs.Last()), c => c.StartsWith("HTTP_PROXY=", StringComparison.Ordinal));
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

        var launch = Env(_api.Execs.Last());
        foreach (var name in new[] { "NO_PROXY", "no_proxy" })
            Assert.Contains(launch, c => c.StartsWith(name + "=", StringComparison.Ordinal) && c.Split('=', 2)[1].Split(',').Contains("ild.example"));
        Assert.Equal("http://ild.example:8080", _host.GatewayApiUrl("http://elsewhere.invalid:1", RunPaths.WorkingDirectory(_run)));
    }

    [Fact]
    public async Task A_run_that_asks_for_dns_still_gets_the_alias_cluster_dns_cannot_resolve()
    {
        _host = NewHost(Options() with { IldUrl = "http://ild:8080" });
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo, Dns: true), TestContext.Current.CancellationToken);

        var spec = _api.Get<V1Pod>("pods", Name(_run))!.Spec;
        Assert.Null(spec.DnsPolicy);
        Assert.Contains("ild-api", Assert.Single(spec.HostAliases).Hostnames);
    }

    [Fact]
    public async Task A_host_that_is_a_suffix_of_the_preview_domain_is_not_put_on_NO_PROXY_and_ild_is_reached_by_another_name()
    {
        _host = NewHost(Options() with { IldUrl = "http://ild:8080" });
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "curl");

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var noProxy = Assert.Single(Env(_api.Execs.Last()), c => c.StartsWith("NO_PROXY=", StringComparison.Ordinal))[9..].Split(',');
        Assert.Contains("ild-api", noProxy);
        Assert.DoesNotContain(noProxy, e => "x.preview.ild".EndsWith(e.TrimStart('.'), StringComparison.Ordinal) && e is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"));
        Assert.Equal("http://ild-api:8080", _host.GatewayApiUrl(null, RunPaths.WorkingDirectory(_run)));
        var mcp = IldMcpServer.BuildEnvironment(null, environment: new RunFacingEnvironment(new TestProcessEnvironment { { "ILD_API_URL", "http://ild:8080" } }, _host, RunPaths.WorkingDirectory(_run)));
        Assert.Contains(new Uri((string)mcp["ILD_API_URL"]!).Host, noProxy);
        var names = Assert.Single(_api.Get<V1Pod>("pods", Name(_run))!.Spec.HostAliases).Hostnames;
        Assert.Contains("ild-api", names);
    }

    private KubernetesRunHost AgentReadHost(string readRoot, Dictionary<string, string> written)
    {
        var host = new KubernetesRunHost(_api.Client(), Options(), FakeKubernetesApi.Namespace, new ProcessRunner(), _log)
        {
            ReadyPollInterval = TimeSpan.FromMilliseconds(10),
            AgentReadRoot = () => readRoot,
        };
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && e.Command.Count > 7 && e.Command[6] == "sh" && e.Command[7].StartsWith("/run/ild/agent-read", StringComparison.Ordinal))
                written[e.Command[7]] = Encoding.UTF8.GetString(await e.ReadStdinToEndAsync());
            await e.ExitAsync(0);
        };
        return host;
    }

    [Fact]
    public async Task Files_an_agent_launch_declares_are_copied_into_the_pod_and_the_launch_is_pointed_at_the_copies()
    {
        await CreateRunAsync();
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-mcp-config"));
        var config = Path.Combine(readRoot, "ild-mcp-config", $"claude-{_run:N}-1.json");
        File.WriteAllText(config, "{\"mcp\":true}");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var psi = Command(WorkingDirectory, "claude", "--mcp-config", config);

        using var process = await host.StartCommandAsync(
            RunCommand.Agent(psi, _run, null, NetworkMode.Off, new Dictionary<string, string?> { ["CFG"] = "--c=" + config }, [config]),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var staged = $"/run/ild/agent-read/ild-mcp-config/claude-{_run:N}-1.json";
        Assert.Equal("{\"mcp\":true}", written[staged]);
        var exec = _api.Execs.Last();
        var launch = Env(exec);
        Assert.Contains(staged, exec.Command);
        Assert.Contains("CFG=--c=" + staged, launch);
        Assert.DoesNotContain(launch.Concat(exec.Command), c => c.Contains(readRoot, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_policy_leaves_out_the_gateway_listener_port_when_it_is_switched_off()
    {
        _host = NewHost(Options() with { GatewayTlsPort = 0 });
        await CreateRunAsync();

        var toIld = _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Spec.Egress[0];
        Assert.Equal([8080, 3128], toIld.Ports.Select(p => int.Parse(p.Port.Value)).ToArray());
    }

    [Fact]
    public async Task A_copilot_launch_reaches_the_listener_where_the_pod_reaches_ild_and_trusts_the_staged_certificate()
    {
        await CreateRunAsync();
        var readRoot = Path.Combine(_scratch, "agent-read");
        var ca = Path.Combine(_scratch, "gateway-tls.pem");
        File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);

        await CopilotLaunch.RunAsync(host, _run, WorkingDirectory, readRoot, ca, "http://ild.example:8080");

        var launch = Env(_api.Execs.Last());
        Assert.Contains("GH_HOST=ild.example:8442", launch);
        var staged = Assert.Single(launch, c => c.StartsWith("NODE_EXTRA_CA_CERTS=", StringComparison.Ordinal))["NODE_EXTRA_CA_CERTS=".Length..];
        Assert.StartsWith(RunPaths.AgentReadDirectory + "/", staged);
        Assert.Equal("-----BEGIN CERTIFICATE-----", written[staged]);
    }

    [Fact]
    public async Task Paths_in_a_prompt_or_declared_outside_the_launchs_own_files_are_not_copied_into_the_pod()
    {
        await CreateRunAsync();
        var readRoot = Path.Combine(_scratch, "agent-read");
        Directory.CreateDirectory(Path.Combine(readRoot, "ild-mcp-config"));
        var others = Path.Combine(readRoot, "ild-mcp-config", $"claude-{Guid.NewGuid():N}-1.json");
        File.WriteAllText(others, "other launch");
        var secret = Path.Combine(_scratch, "auth.json");
        File.WriteAllText(secret, "login");
        var written = new Dictionary<string, string>();
        var host = AgentReadHost(readRoot, written);
        var prompt = $"read {readRoot}/ then {others} and {readRoot}/../auth.json";
        var psi = Command(WorkingDirectory, "claude", "-p", prompt);

        using var process = await host.StartCommandAsync(
            RunCommand.Agent(psi, _run, null, NetworkMode.Off,
                new Dictionary<string, string?> { ["CFG"] = $"{readRoot}/../auth.json" },
                [others, $"{readRoot}/ild-mcp-config/../../auth.json", Path.Combine(readRoot, "ild-mcp-config"), readRoot]),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(written);
        var exec = _api.Execs.Last();
        Assert.Contains(prompt, exec.Command);
        Assert.Contains($"CFG={readRoot}/../auth.json", Env(exec));
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
        var launch = Env(_api.Execs.Last());
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
        var launch = Env(_api.Execs.Last());
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

            var launch = Env(_api.Execs.Last());
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
    public async Task None_of_ilds_secrets_or_inherited_variables_reach_a_command_in_the_pod(bool asAgent)
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
            var launch = Env(exec);
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
    public async Task An_agent_launch_in_a_proxied_mode_gets_the_proxy_at_ilds_host_in_the_pod(NetworkMode mode)
    {
        await CreateRunAsync();
        var providerId = Guid.NewGuid();

        using var process = await _host.StartCommandAsync(
            RunCommand.Agent(Command(WorkingDirectory, "claude", "-p", "hi"), _run, providerId, mode, null),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains(Env(_api.Execs.Last()), e => e.StartsWith("HTTPS_PROXY=http://ild:", StringComparison.Ordinal) && e.EndsWith("@ild.example:3128", StringComparison.Ordinal));
        Assert.Equal(new LaunchScope(LaunchKind.Run, _run, providerId), AgentIsolationEgressTests.ScopeInEnvironment(Env(_api.Execs.Last()), EgressProxyTests.Creds));
    }

    [Fact]
    public async Task No_exec_url_carries_the_launchs_proxy_credential_which_travels_in_the_pods_env_file()
    {
        await CreateRunAsync();

        using var process = await _host.StartCommandAsync(
            RunCommand.Agent(Command(WorkingDirectory, "claude", "-p", "hi"), _run, Guid.NewGuid(), NetworkMode.Monitor, null),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        // The credential is the password of the proxy URL, which is in the environment of the launch.
        var proxyUrl = Assert.Single(Env(_api.Execs.Last()), e => e.StartsWith("HTTPS_PROXY=", StringComparison.Ordinal))["HTTPS_PROXY=".Length..];
        var credential = new Uri(proxyUrl).UserInfo.Split(':', 2)[1];
        Assert.True(credential.Length >= 16);
        Assert.NotEmpty(_api.Execs);
        foreach (var exec in _api.Execs)
        {
            // What the API server logs for an exec is its URL: the pod, the container and the command's arguments.
            Assert.DoesNotContain(credential, exec.Joined, StringComparison.Ordinal);
            Assert.DoesNotContain("HTTPS_PROXY", exec.Joined, StringComparison.Ordinal);
        }
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
        Assert.Equal(KubernetesRunHost.KillAllScript, kill.Command[5]);
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
        Assert.Contains("kill -s TERM", kill.Command[5]);
        hold.SetResult();
    }

    // -- Delete and sweep --------------------------------------------------------

    [Fact]
    public async Task Delete_removes_the_pod_first_then_the_owner_and_the_cluster_garbage_collects_the_rest()
    {
        var env = await CreateRunAsync();
        var violations = GuardPolicies();

        await _host.DeleteAsync(env, TestContext.Current.CancellationToken);

        Assert.Equal([$"pods/{Name(_run)}", $"configmaps/{Name(_run)}"], _api.Deleted);
        Assert.Empty(_api.Names("pods"));
        Assert.Empty(_api.Names("persistentvolumeclaims"));
        Assert.Empty(_api.Names("networkpolicies"));
        Assert.Empty(violations);
    }

    // Records every NetworkPolicy delete that arrives while the pod it protects still exists.
    private List<string> GuardPolicies()
    {
        var violations = new List<string>();
        _api.BeforeDelete = what =>
        {
            if (what.StartsWith("networkpolicies/", StringComparison.Ordinal))
            {
                var policy = what["networkpolicies/".Length..];
                var pod = policy.EndsWith("-open", StringComparison.Ordinal) ? policy[..^"-open".Length] : policy;
                if (_api.Get<V1Pod>("pods", pod) is not null) violations.Add(what);
            }
            return Task.CompletedTask;
        };
        return violations;
    }

    [Fact]
    public async Task A_pod_stuck_terminating_is_deleted_with_a_grace_period_of_0_and_the_policies_go_only_after_that()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = NewHost(Options() with { ReadyTimeout = TimeSpan.FromMilliseconds(300) });
        var env = await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), ct);
        _api.Get<V1Pod>("pods", Name(_run))!.Metadata.DeletionTimestamp = DateTime.UtcNow;
        var violations = GuardPolicies();
        _api.Deleted.Clear();

        await host.DeleteAsync(env, ct);

        Assert.Equal([$"pods/{Name(_run)}", $"pods/{Name(_run)}", $"configmaps/{Name(_run)}"], _api.Deleted);
        Assert.Empty(_api.Names("networkpolicies"));
        Assert.Empty(violations);
    }

    [Fact]
    public async Task The_leftover_sweep_deletes_a_runs_policies_only_after_its_pod_is_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = NewHost(Options() with { ReadyTimeout = TimeSpan.FromMilliseconds(300) });
        var dead = Guid.NewGuid();
        SeedRun(dead, Instance);
        _api.Seed("networkpolicies", KubernetesNames.OpenName(Name(dead)), Instance, dead);
        _api.Get<V1Pod>("pods", Name(dead))!.Metadata.DeletionTimestamp = DateTime.UtcNow;
        var violations = GuardPolicies();

        Assert.True(await host.DeleteLeftoverAsync(new RunLeftover(KubernetesRunHost.RunLeftoverKind, dead.ToString("N")), ct));

        Assert.Empty(_api.Names("pods"));
        Assert.Empty(_api.Names("networkpolicies"));
        Assert.Contains($"networkpolicies/{Name(dead)}", _api.Deleted);
        Assert.Empty(violations);
    }

    [Fact]
    public async Task An_installers_policy_is_deleted_only_after_its_pod_is_gone()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);
        var violations = GuardPolicies();
        AnswerInstallers(AgentNodeLine, PiLine, NodeLine);

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        Assert.Contains(_api.Deleted, d => d.StartsWith("networkpolicies/ild-tools-", StringComparison.Ordinal));
        Assert.Empty(violations);
    }

    [Fact]
    public async Task A_sign_in_pod_has_an_egress_policy_made_before_it_and_deleted_only_after_it_is_gone()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && !e.Tty) await e.ReadStdinToEndAsync();
            if (!e.Tty) await e.ExitAsync(0);
        };
        var ct = TestContext.Current.CancellationToken;
        var violations = GuardPolicies();

        var signIn = await _host.OpenSignInAsync(null, "claude", [], 80, 24, ct);
        var pod = SignInPod();
        var policy = _api.Get<V1NetworkPolicy>("networkpolicies", pod)!;
        Assert.True(_api.Created.IndexOf($"networkpolicies/{pod}") < _api.Created.IndexOf($"pods/{pod}"));
        Assert.Equal(["Ingress", "Egress"], policy.Spec.PolicyTypes);
        Assert.Empty(policy.Spec.Ingress);
        Assert.Empty(Assert.Single(policy.Spec.Egress).To ?? []);
        Assert.Equal(_api.Get<V1Pod>("pods", pod)!.Metadata.Labels["ild/signin"], policy.Spec.PodSelector.MatchLabels["ild/signin"]);

        await signIn.DisposeAsync();

        Assert.Empty(_api.Names("pods"));
        Assert.Empty(_api.Names("networkpolicies"));
        Assert.Empty(violations);
    }

    [Fact]
    public async Task Shutdown_deletes_a_sign_in_pods_policy_after_the_pod()
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin && !e.Tty) await e.ReadStdinToEndAsync();
            if (!e.Tty) await e.ExitAsync(0);
        };
        var ct = TestContext.Current.CancellationToken;
        await using var signIn = await _host.OpenSignInAsync(null, "claude", [], 80, 24, ct);
        var violations = GuardPolicies();

        await _host.ShutdownAsync(ct);

        Assert.Empty(_api.Names("networkpolicies"));
        Assert.Empty(violations);
    }

    [Fact]
    public async Task A_sign_in_policy_left_without_its_pod_is_a_leftover_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "ild-signin-" + Guid.NewGuid().ToString("N");
        _api.SeedSignIn(name, Instance, "networkpolicies");

        var leftover = Assert.Single(await _host.ListLeftoversAsync(new HashSet<Guid>(), ct));

        Assert.Equal(name, leftover.Location);
        Assert.True(await _host.DeleteLeftoverAsync(leftover, ct));
        Assert.Empty(_api.Names("networkpolicies"));
    }

    [Fact]
    public async Task The_owner_is_a_config_map_and_every_owner_reference_is_of_that_kind()
    {
        await CreateRunAsync();
        using var process = await StartAsync(NetworkMode.Off);

        var owner = _api.Get<V1ConfigMap>("configmaps", Name(_run))!;
        Assert.Equal(_run.ToString("N"), owner.Data["run"]);
        foreach (var meta in new[]
        {
            _api.Get<V1PersistentVolumeClaim>("persistentvolumeclaims", Name(_run))!.Metadata,
            _api.Get<V1NetworkPolicy>("networkpolicies", Name(_run))!.Metadata,
            _api.Get<V1NetworkPolicy>("networkpolicies", OpenPolicyName)!.Metadata,
            _api.Get<V1Pod>("pods", Name(_run))!.Metadata,
        })
            Assert.Equal("ConfigMap", Assert.Single(meta.OwnerReferences).Kind);
    }

    [Fact]
    public async Task The_settings_warning_names_a_missing_default_deny_and_is_silent_when_it_is_there()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Contains("default-deny", await _host.WarningAsync(ct));

        // Other policies do not count, nor one that allows something.
        _api.Seed("networkpolicies", "other", Instance, Guid.NewGuid());
        Assert.NotNull(await _host.WarningAsync(ct));

        _api.SeedPolicy("ild-default-deny", new V1NetworkPolicySpec
        {
            PodSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "ild" } },
            PolicyTypes = ["Ingress", "Egress"],
            Egress = [new V1NetworkPolicyEgressRule()],
        });
        Assert.NotNull(await _host.WarningAsync(ct));

        _api.SeedPolicy("ild-default-deny", new V1NetworkPolicySpec
        {
            PodSelector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { ["app.kubernetes.io/managed-by"] = "ild" } },
            PolicyTypes = ["Ingress", "Egress"],
        });
        Assert.Null(await _host.WarningAsync(ct));
    }

    [Fact]
    public async Task The_settings_warning_says_nothing_when_the_policies_cannot_be_listed()
    {
        await _api.DisposeAsync();
        _api = await FakeKubernetesApi.StartAsync();
        var host = new KubernetesRunHost(new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = "http://127.0.0.1:1" }), Options(), FakeKubernetesApi.Namespace, new ProcessRunner(), _log);

        Assert.Null(await host.WarningAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_of_a_run_that_is_already_gone_is_not_an_error()
    {
        await _host.DeleteAsync(new RunEnvironment(Guid.NewGuid(), "/ild-run/x/repo"), TestContext.Current.CancellationToken);

        Assert.Empty(_api.Deleted);
    }

    private void SeedRun(Guid run, string instance, bool secret = true, string managedBy = "ild")
    {
        if (secret) _api.Seed("configmaps", Name(run), instance, run, managedBy);
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

        foreach (var kind in new[] { "configmaps", "pods", "persistentvolumeclaims", "networkpolicies" })
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

    private const string GatewayToken = "gateway-token-value-1";
    private const string ApiToken = "ild-api-token-value-2";

    private static Dictionary<string, string?> Secrets() => new() { ["ANTHROPIC_API_KEY"] = GatewayToken, ["ILD_API_TOKEN"] = ApiToken };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_secret_of_a_commands_environment_is_in_any_exec_command_or_url_it_travels_only_in_a_private_file_written_over_stdin(bool asAgent)
    {
        await CreateRunAsync();
        var psi = Command(WorkingDirectory, "claude", "-p", "hi");

        using var process = await _host.StartCommandAsync(
            asAgent ? RunCommand.Agent(psi, _run, null, NetworkMode.Monitor, Secrets()) : RunCommand.Prepared(psi, _run, Secrets()),
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_api.Execs.SelectMany(e => e.Command), c => c.Contains(GatewayToken, StringComparison.Ordinal) || c.Contains(ApiToken, StringComparison.Ordinal));
        var launch = LaunchExec();
        Assert.Contains($"ANTHROPIC_API_KEY={GatewayToken}", Env(launch));
        Assert.Contains($"ILD_API_TOKEN={ApiToken}", Env(launch));
        // The one place the secret is sent: the write of the file, by a command that has no secret in it, to a path only the command's wrapper is told.
        var write = Assert.Single(_api.Execs, e => e.Command.Contains(ExecRunHost.WriteEnvironmentFileScript));
        Assert.Contains(GatewayToken, write.StdinSoFar());
        Assert.StartsWith(RunPaths.ProcessDirectory + "/", write.Command[^1]);
        Assert.Equal(write.Command[^1], launch.Command[6]);
        Assert.Contains("umask 077", ExecRunHost.WriteEnvironmentFileScript);
        // The command's own stdin is its own.
        Assert.DoesNotContain(GatewayToken, launch.StdinSoFar());
    }

    [Fact]
    public async Task No_secret_of_a_terminals_environment_is_in_the_exec_command_url_or_typed_into_the_terminal()
    {
        await CreateRunAsync();

        using var pty = await _host.OpenTerminalAsync(
            new PtyOptions
            {
                Cwd = WorkingDirectory, App = "/bin/bash", Cols = 80, Rows = 24, Name = "t",
                Environment = new Dictionary<string, string> { ["ILD_API_TOKEN"] = ApiToken },
            }, TestContext.Current.CancellationToken);

        var terminal = _api.Execs.Last();
        Assert.True(terminal.Tty);
        Assert.DoesNotContain(_api.Execs.SelectMany(e => e.Command), c => c.Contains(ApiToken, StringComparison.Ordinal));
        Assert.DoesNotContain(ApiToken, terminal.StdinSoFar());
        var write = Assert.Single(_api.Execs, e => e.Command.Contains(ExecRunHost.WriteEnvironmentFileScript));
        Assert.Contains($"export ILD_API_TOKEN='{ApiToken}'", write.StdinSoFar());
        Assert.Equal(write.Command[^1], terminal.Command[5]);
        Assert.StartsWith($"{RunPaths.ProcessDirectory}/{_run:N}/", write.Command[^1]);
    }

    [Fact]
    public async Task A_command_that_reads_its_own_stdin_gets_it_and_nothing_else_with_an_environment_too()
    {
        await CreateRunAsync();
        _api.OnExec = async e =>
        {
            if (e.Command.Contains(ExecRunHost.WriteEnvironmentFileScript)) { await e.ReadStdinToEndAsync(); await e.ExitAsync(0); return; }
            var input = Encoding.UTF8.GetString(await e.ReadStdinToEndAsync());
            await e.WriteStdoutAsync(input);
            await e.ExitAsync(0);
        };
        var psi = Command(WorkingDirectory, "cat");
        psi.RedirectStandardInput = true;

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(psi, _run, Secrets()), TestContext.Current.CancellationToken);
        await process.StandardInput.WriteAsync("prompt text");
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("prompt text", await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("prompt text", LaunchExec().StdinSoFar());
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
        Assert.Equal($"HOME={RunPaths.HomeDirectory(_run)}", Env(exec).Single(c => c.StartsWith("HOME=", StringComparison.Ordinal)));
        Assert.DoesNotContain(Env(exec), c => c.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal("/caller/bin", exec.Command[7]);
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
        => Assert.Equal("http://ild.example:8080", _host.GatewayApiUrl(null, RunPaths.WorkingDirectory(_run)));

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
    public async Task Paths_on_ilds_disk_are_read_locally_but_never_run_and_loop_runs_go_to_the_pod()
    {
        await CreateRunAsync();
        _api.Execs.Clear();
        var local = FakeLocal().WithDirectory("/data/chat-sessions/c1");
        var routing = new RoutingRunHost(local, _host);
        var chat = Guid.NewGuid();

        Assert.True(await routing.DirectoryExistsAsync("/data/chat-sessions/c1", TestContext.Current.CancellationToken));
        Assert.Empty(_api.Execs);

        var chatCommand = RunCommand.Prepared(Command("/data/chat-sessions/c1", "git", "status"), chat);
        await Assert.ThrowsAsync<PredatesContainersException>(() => routing.StartCommandAsync(chatCommand, TestContext.Current.CancellationToken));
        Assert.Empty(local.Started);
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
            Assert.Equal("chat", labels[RunPaths.RoleLabel]);
            Assert.Equal(Instance, labels[RunPaths.InstanceLabel]);
            Assert.False(labels.ContainsKey(RunPaths.RunLabel));
        }
        Assert.Null(claim.Metadata.OwnerReferences);
        Assert.Equal("run-scratch", claim.Spec.StorageClassName);
        Assert.Equal(policy.Spec.PodSelector.MatchLabels, new Dictionary<string, string> { [RunPaths.RoleLabel] = "chat", [RunPaths.InstanceLabel] = Instance });
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
    public async Task The_chat_pod_mounts_the_agents_and_toolchain_zones_read_only_whenever_there_is_a_store_and_never_a_repositorys()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));

        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", TestContext.Current.CancellationToken);

        var pod = _api.Get<V1Pod>("pods", ChatPodName)!;
        Assert.True(pod.Spec.Volumes.Single(v => v.Name == "tools").PersistentVolumeClaim.ReadOnlyProperty);
        var mounts = pod.Spec.Containers.Single().VolumeMounts.Where(v => v.Name == "tools").OrderBy(m => m.MountPath).ToList();
        Assert.Equal([(RunToolStore.ToolchainsRoot, "toolchains"), (RunToolStore.Root, "ild")], mounts.Select(m => (m.MountPath, m.SubPath)));
        Assert.All(mounts, m => Assert.True(m.ReadOnlyProperty));
    }

    [Fact]
    public async Task A_chat_does_not_get_the_tools_only_a_repository_may_hold()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));
        var chat = Guid.NewGuid();
        var installs = AnswerInstallers(NodeLine, PrettierLine(PrettierFolder));
        var repo = MakeBaseRepository(("package.json", "{}"), ("mise.toml", "[tools]\n\"npm:prettier\" = \"3\"\n"));

        await host.AddChatToolsAsync(ChatFolder(chat), repo, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(installs, i => i.Command.Any(a => a.Contains("npm:", StringComparison.Ordinal)));
        Assert.Contains(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("npm:prettier@3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_scripts_are_off_in_the_agents_and_toolchain_zones_and_on_in_a_repositorys_unless_it_opts_out()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);
        var installs = AnswerInstallers(AgentNodeLine, PiLine, NodeLine, PrettierLine(PrettierFolder));
        var repo = MakeBaseRepository(("package.json", "{}"), ("mise.toml", "[tools]\n\"npm:prettier\" = \"3\"\n"));

        await host.CreateAsync(new RunEnvironmentRequest(_run, repo, "b", Repo), TestContext.Current.CancellationToken);

        foreach (var (pod, command) in installs)
        {
            var env = Installer(pod).Spec.Containers.Single().Env;
            var ignores = env.Any(e => e.Name == "npm_config_ignore_scripts" && e.Value == "true");
            Assert.Equal(!command.Contains("tool=npm:prettier@3"), ignores);
        }
    }

    [Fact]
    public async Task A_repository_that_opts_out_of_install_scripts_gets_them_off_in_its_zone_too()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));
        var folder = RunToolStore.RepositoryFolder([new RunTool("npm:prettier", "3")], true);
        var installs = AnswerInstallers(NodeLine, PrettierLine(folder));
        var repo = MakeBaseRepository(("package.json", "{}"), ("mise.toml", "[tools]\n\"npm:prettier\" = \"3\"\n"), ("ild.config.json", """{"run":{"ignoreScripts":true}}"""));

        await host.CreateAsync(new RunEnvironmentRequest(_run, repo, "b", Repo), TestContext.Current.CancellationToken);

        var installer = Installer(installs.Single(i => i.Command.Contains("tool=npm:prettier@3")).Pod);
        Assert.Contains(installer.Spec.Containers.Single().Env, e => e.Name == "npm_config_ignore_scripts" && e.Value == "true");
        Assert.Equal(folder, EnvOf(installer, "ILD_TOOLS_ROOT"));
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
            await client.CoreV1.DeleteNamespacedPodAsync(ChatPodName, FakeKubernetesApi.Namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: TestContext.Current.CancellationToken);
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
        Assert.Equal(ExecRunHost.KillAllScript, kill.Command[5]);
    }

    // -- Idle chat pod -----------------------------------------------------------

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private (KubernetesRunHost Host, ManualTimeProvider Clock) IdleHost(TimeSpan? timeout = null)
    {
        var clock = new ManualTimeProvider();
        return (NewHost(Options() with { ChatIdleTimeout = timeout ?? IdleTimeout }, time: clock), clock);
    }

    [Fact]
    public async Task An_idle_chat_pod_is_deleted_after_the_timeout_and_its_claim_and_policy_stay_and_the_next_chat_makes_it_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        var chat = Guid.NewGuid();
        await host.CreateChatFolderAsync(chat, "/unused", ct);

        clock.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);
        Assert.Contains(ChatPodName, _api.Names("pods"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);

        Assert.DoesNotContain(ChatPodName, _api.Names("pods"));
        Assert.Contains(ChatPodName, _api.Names("persistentvolumeclaims"));
        Assert.Contains(ChatPodName, _api.Names("networkpolicies"));
        Assert.Contains(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("Removed the chat pod", StringComparison.Ordinal));

        Assert.True(await host.DirectoryExistsAsync(ChatFolder(chat), ct));

        Assert.Contains(ChatPodName, _api.Names("pods"));
        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{ChatPodName}"));
        Assert.Equal(1, _api.Created.Count(c => c == $"persistentvolumeclaims/{ChatPodName}"));
        Assert.Contains(_log.At(Microsoft.Extensions.Logging.LogLevel.Information), m => m.Contains("is back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_chat_pod_is_not_deleted_while_a_chat_command_runs_or_a_chat_hold_is_open_and_the_time_counts_from_when_they_end()
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

        using var process = await host.StartCommandAsync(RunCommand.Prepared(Command(ChatFolder(chat), "npm", "run", "dev"), chat), ct);
        clock.Advance(IdleTimeout * 2);
        await host.ReapIdleChatAsync(ct);
        Assert.Contains(ChatPodName, _api.Names("pods"));

        release.SetResult();
        await process.WaitForExitAsync(ct);
        using (host.HoldChat(ChatFolder(chat)))
        {
            clock.Advance(IdleTimeout * 2);
            await host.ReapIdleChatAsync(ct);
            Assert.Contains(ChatPodName, _api.Names("pods"));
        }

        clock.Advance(IdleTimeout - TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);
        Assert.Contains(ChatPodName, _api.Names("pods"));

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.ReapIdleChatAsync(ct);
        Assert.DoesNotContain(ChatPodName, _api.Names("pods"));
    }

    [Fact]
    public async Task A_chat_that_starts_while_the_reaper_deletes_the_chat_pod_waits_for_it_and_gets_a_new_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        var chat = Guid.NewGuid();
        await host.CreateChatFolderAsync(chat, "/unused", ct);
        var deleting = new TaskCompletionSource();
        var proceed = new TaskCompletionSource();
        _api.BeforeDelete = async what =>
        {
            if (what == $"pods/{ChatPodName}" && !deleting.Task.IsCompleted)
            {
                deleting.SetResult();
                await proceed.Task;
            }
        };
        clock.Advance(IdleTimeout);

        var reaping = host.ReapIdleChatAsync(ct);
        await deleting.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var starting = host.DirectoryExistsAsync(ChatFolder(chat), ct);
        await Task.Delay(100, ct);
        Assert.False(starting.IsCompleted);
        proceed.SetResult();
        await reaping;

        Assert.True(await starting);
        Assert.Contains(ChatPodName, _api.Names("pods"));
        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{ChatPodName}"));
    }

    [Fact]
    public async Task Run_pods_are_never_touched_by_the_idle_deletion_whatever_state_their_run_is_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        var running = Guid.NewGuid();
        var awaitingHuman = Guid.NewGuid();
        var parked = Guid.NewGuid();
        _api.Seed("pods", Name(running), Instance, running, podPhase: "Running");
        _api.Seed("pods", Name(awaitingHuman), Instance, awaitingHuman, podPhase: "Running");
        _api.Seed("pods", Name(parked), Instance, parked, podPhase: "Succeeded");
        foreach (var run in new[] { running, awaitingHuman, parked })
            _api.Seed("persistentvolumeclaims", Name(run), Instance, run);
        var execs = _api.Execs.Count;

        clock.Advance(IdleTimeout * 10);
        await host.ReapIdleChatAsync(ct);

        Assert.DoesNotContain(ChatPodName, _api.Names("pods"));
        Assert.Equal(new[] { Name(awaitingHuman), Name(parked), Name(running) }.Order(), _api.Names("pods").Order());
        Assert.Equal([$"pods/{ChatPodName}"], _api.Deleted);
        Assert.Equal(execs, _api.Execs.Count);
    }

    [Fact]
    public async Task The_chat_pod_stays_when_the_idle_timeout_is_off()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost(TimeSpan.Zero);
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);

        clock.Advance(TimeSpan.FromDays(30));
        await host.ReapIdleChatAsync(ct);

        Assert.Contains(ChatPodName, _api.Names("pods"));
        Assert.Empty(_api.Deleted);
    }

    [Fact]
    public async Task The_sweep_deletes_an_idle_chat_pod()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, clock) = IdleHost();
        await host.CreateChatFolderAsync(Guid.NewGuid(), "/unused", ct);
        var sweeper = new RunHostSweeper(new RoutingRunHost(FakeLocal(), host), _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()), TimeSpan.FromMinutes(1));

        clock.Advance(IdleTimeout);
        await sweeper.SweepAsync(host, ct);

        Assert.DoesNotContain(ChatPodName, _api.Names("pods"));
    }

    [Fact]
    public void The_idle_timeout_is_thirty_minutes_unless_set_and_zero_turns_it_off()
    {
        KubernetesRunHostOptions From(string? minutes) => KubernetesRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => "http://ild.example:8080",
            "ILD_K8S_RUNNER_IMAGE" => "img",
            "ILD_CHAT_IDLE_TIMEOUT_MINUTES" => minutes,
            _ => null,
        });

        Assert.Equal(TimeSpan.FromMinutes(30), From(null).ChatIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), From("5").ChatIdleTimeout);
        Assert.Equal(TimeSpan.Zero, From("0").ChatIdleTimeout);
        Assert.Throws<InvalidOperationException>(() => From("-1"));
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
                    {"kind":"tool","spec":"node@20","version":"20.19.1","path":["/opt/ild-toolchains/mise/installs/node/20.19.1/bin"],"env":{}}

                    """);
            await e.ExitAsync(0);
        };

        await host.AddChatToolsAsync(ChatFolder(chat), MakeBaseRepository(("package.json", "{}"), (".nvmrc", "v20\n")), TestContext.Current.CancellationToken);
        Assert.Contains(_api.Created, c => c.StartsWith("pods/ild-tools-", StringComparison.Ordinal));

        using var process = await host.StartCommandAsync(RunCommand.Prepared(Command(ChatFolder(chat), "node", "-v"), chat), TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var exec = _api.Execs.Last();
        Assert.Equal(ChatPodName, exec.Pod);
        Assert.StartsWith("PATH=/opt/ild-toolchains/mise/installs/node/20.19.1/bin:", Env(exec).Single(c => c.StartsWith("PATH=", StringComparison.Ordinal)), StringComparison.Ordinal);
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
        Assert.DoesNotContain(Name(Guid.ParseExact(leftover.Location, "N")), _api.Names("configmaps"));
    }

    private async Task<RunEnvironment> CreateRunThroughAsync(IRunHost host)
    {
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            await e.ExitAsync(0);
        };
        return await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b", Repo), TestContext.Current.CancellationToken);
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
        Assert.Equal(_run, RunPaths.OwnerOf($"/ild-run/{_run:N}/repo/src/a.cs"));
        Assert.Equal(_run, RunPaths.OwnerOf($"/ild-run/{_run:N}"));
        Assert.Null(RunPaths.OwnerOf("/ild-run/not-a-run/repo"));
        Assert.Null(RunPaths.OwnerOf("/worktrees/ild/repo"));
        Assert.Null(RunPaths.OwnerOf(null));
    }

    // -- A run pod that ended ------------------------------------------------------

    private async Task<V1Pod> EndRunPodAsync(string phase)
    {
        await CreateRunAsync();
        var pod = _api.Get<V1Pod>("pods", Name(_run))!;
        pod.Status = new V1PodStatus { Phase = phase, Reason = "Evicted" };
        return pod;
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Succeeded")]
    public async Task A_command_in_an_ended_run_pod_makes_the_pod_again_on_the_same_claim_and_keeps_the_secret_and_volume(string phase)
    {
        var ended = await EndRunPodAsync(phase);
        var claimName = ended.Spec.Volumes.Single(v => v.Name == "work").PersistentVolumeClaim.ClaimName;
        var ct = TestContext.Current.CancellationToken;

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "echo", "hi"), _run), ct);
        await process.WaitForExitAsync(ct);

        Assert.Contains($"pods/{Name(_run)}", _api.Deleted);
        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{Name(_run)}"));
        var fresh = _api.Get<V1Pod>("pods", Name(_run))!;
        Assert.Equal(claimName, fresh.Spec.Volumes.Single(v => v.Name == "work").PersistentVolumeClaim.ClaimName);
        Assert.Equal(ended.Metadata.OwnerReferences.Single().Name, fresh.Metadata.OwnerReferences.Single().Name);
        Assert.Equal("Running", fresh.Status?.Phase ?? "Running");
        Assert.DoesNotContain($"configmaps/{Name(_run)}", _api.Deleted);
        Assert.DoesNotContain($"persistentvolumeclaims/{Name(_run)}", _api.Deleted);
        Assert.Single(_api.Names("configmaps"));
        Assert.Single(_api.Names("persistentvolumeclaims"));
    }

    [Fact]
    public async Task A_run_pod_stuck_terminating_on_a_lost_node_is_removed_with_a_grace_period_of_0_and_made_again()
    {
        var ended = await EndRunPodAsync("Failed");
        ended.Metadata.DeletionTimestamp = DateTime.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        using var process = await _host.StartCommandAsync(RunCommand.Prepared(Command(WorkingDirectory, "echo", "hi"), _run), ct);
        await process.WaitForExitAsync(ct);
        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepositoryIn("again"), "ild/wi-1-run", Repo), ct);

        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{Name(_run)}"));
        Assert.Null(_api.Get<V1Pod>("pods", Name(_run))!.Metadata.DeletionTimestamp);
        Assert.Single(_api.Names("persistentvolumeclaims"));
    }

    [Fact]
    public async Task A_start_retried_on_a_run_pod_stuck_terminating_removes_it_with_a_grace_period_of_0()
    {
        var ended = await EndRunPodAsync("Failed");
        ended.Metadata.DeletionTimestamp = DateTime.UtcNow;

        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepositoryIn("again"), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{Name(_run)}"));
        Assert.Single(_api.Names("configmaps"));
    }

    [Fact]
    public async Task A_start_retried_on_an_ended_run_pod_replaces_the_pod_and_keeps_the_volume()
    {
        await EndRunPodAsync("Failed");

        await _host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepositoryIn("again"), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken);

        Assert.Contains($"pods/{Name(_run)}", _api.Deleted);
        Assert.Equal(2, _api.Created.Count(c => c == $"pods/{Name(_run)}"));
        Assert.DoesNotContain($"configmaps/{Name(_run)}", _api.Deleted);
        Assert.DoesNotContain($"persistentvolumeclaims/{Name(_run)}", _api.Deleted);
        Assert.Single(_api.Names("persistentvolumeclaims"));
    }

    [Fact]
    public async Task A_start_that_fails_on_objects_it_did_not_make_leaves_them_for_the_next_try()
    {
        await EndRunPodAsync("Failed");
        _api.PodReadsBeforeReady = int.MaxValue;
        var host = NewHost(Options() with { ReadyTimeout = TimeSpan.FromMilliseconds(200) });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepositoryIn("again"), "ild/wi-1-run", Repo), TestContext.Current.CancellationToken));

        Assert.DoesNotContain($"configmaps/{Name(_run)}", _api.Deleted);
        Assert.Single(_api.Names("configmaps"));
        Assert.Single(_api.Names("persistentvolumeclaims"));
    }

    // -- Installers left by a crash ------------------------------------------------

    [Fact]
    public async Task Installer_pods_ILD_has_not_got_open_are_listed_as_leftovers_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        _api.SeedTools("ild-tools-" + Guid.NewGuid().ToString("N"), Instance);
        _api.SeedTools("ild-tools-" + Guid.NewGuid().ToString("N"), "other");
        var orphan = Assert.Single(_api.Names("pods"), n => n.StartsWith("ild-tools-", StringComparison.Ordinal) && _api.Get<V1Pod>("pods", n)!.Metadata.Labels["ild/instance"] == Instance);

        var leftover = Assert.Single(await _host.ListLeftoversAsync(new HashSet<Guid>(), ct));

        Assert.Equal(orphan, leftover.Location);
        Assert.True(await _host.DeleteLeftoverAsync(leftover, ct));
        Assert.Contains($"pods/{orphan}", _api.Deleted);
    }

    [Fact]
    public async Task An_installers_network_policy_left_without_its_pod_is_a_leftover_and_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = "ild-tools-" + Guid.NewGuid().ToString("N");
        _api.SeedTools(name, Instance, "networkpolicies");

        var leftover = Assert.Single(await _host.ListLeftoversAsync(new HashSet<Guid>(), ct));

        Assert.Equal(name, leftover.Location);
        Assert.True(await _host.DeleteLeftoverAsync(leftover, ct));
        Assert.Contains($"networkpolicies/{name}", _api.Deleted);
    }

    [Fact]
    public async Task An_installer_pod_that_is_open_is_not_swept_and_has_requests_and_no_limits()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));
        var ct = TestContext.Current.CancellationToken;
        var seen = new List<string>();
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Pod.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                _installerPods[e.Pod] = _api.Get<V1Pod>("pods", e.Pod)!;
                seen.AddRange((await host.ListLeftoversAsync(new HashSet<Guid>(), ct)).Select(l => l.Location));
                await e.WriteStdoutAsync(NodeLine + "\n");
            }
            await e.ExitAsync(0);
        };

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(("package.json", "{}")), "b", Repo), ct);

        Assert.Empty(seen);
        var resources = Installer(Assert.Single(_installerPods.Keys)).Spec.Containers.Single().Resources;
        Assert.Equal("100m", resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", resources.Requests["memory"].ToString());
        Assert.Null(resources.Limits);
    }

    [Fact]
    public async Task Shutdown_deletes_the_installer_pods_that_are_open()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"));
        var ct = TestContext.Current.CancellationToken;
        _api.OnExec = async e =>
        {
            if (e.WantsStdin) await e.ReadStdinToEndAsync();
            if (e.Pod.StartsWith("ild-tools-", StringComparison.Ordinal))
            {
                await host.ShutdownAsync(ct);
                await e.WriteStdoutAsync(NodeLine + "\n");
            }
            await e.ExitAsync(0);
        };

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(("package.json", "{}")), "b", Repo), ct);

        Assert.Contains(_api.Deleted, d => d.StartsWith("pods/ild-tools-", StringComparison.Ordinal));
    }

    // -- Limits and sign-in --------------------------------------------------------

    [Fact]
    public async Task A_limit_of_zero_means_no_limit_and_the_pod_has_no_limits_key()
    {
        var options = KubernetesRunHostOptions.From(name => name switch
        {
            "ILD_API_URL" => "http://ild.example:8080",
            "ILD_K8S_RUNNER_IMAGE" => "img",
            "ILD_RUN_CPU_LIMIT" => "0",
            "ILD_RUN_MEMORY_LIMIT" => "0",
            _ => null,
        }) with { ReadyTimeout = TimeSpan.FromSeconds(10) };
        var host = NewHost(options);

        await host.CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b", Repo), TestContext.Current.CancellationToken);

        var resources = _api.Get<V1Pod>("pods", Name(_run))!.Spec.Containers.Single().Resources;
        Assert.Null(resources.Limits);
        Assert.Equal("100m", resources.Requests["cpu"].ToString());
        Assert.Null(options.CpuLimit);
        Assert.Null(options.MemoryLimit);
    }

    [Fact]
    public async Task One_limit_of_zero_leaves_out_only_that_limit()
    {
        var options = Options() with { CpuLimit = null };
        await NewHost(options).CreateAsync(new RunEnvironmentRequest(_run, MakeBaseRepository(), "b", Repo), TestContext.Current.CancellationToken);

        var limits = _api.Get<V1Pod>("pods", Name(_run))!.Spec.Containers.Single().Resources.Limits;
        Assert.Equal(["memory"], limits.Keys);
    }

    [Fact]
    public async Task A_sign_in_installs_only_the_agent_it_is_for_and_its_pod_has_requests_and_no_limits()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0"), new RunAgent(ManagedAgentCatalog.ClaudeCode, "2.0.0")]);
        var installs = AnswerInstallers(AgentNodeLine, PiLine);
        var ct = TestContext.Current.CancellationToken;

        await using var signIn = await host.OpenSignInAsync(ManagedAgentCatalog.Pi, "pi", [], 80, 24, ct);

        var install = Assert.Single(installs);
        Assert.Contains(install.Command, c => c.StartsWith("agent=pi|", StringComparison.Ordinal));
        Assert.DoesNotContain(install.Command, c => c.StartsWith("agent=claude", StringComparison.Ordinal));
        var pod = _api.Get<V1Pod>("pods", SignInPod())!;
        var resources = pod.Spec.Containers.Single().Resources;
        Assert.Equal("100m", resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", resources.Requests["memory"].ToString());
        Assert.Null(resources.Limits);
        Assert.Contains("/wrap", EnvOf(pod, "PATH"));
    }

    [Fact]
    public async Task A_sign_in_for_no_managed_agent_installs_none()
    {
        var host = NewHost(Options(toolsClaim: "ild-tools"), [new RunAgent(ManagedAgentCatalog.Pi, "1.5.0")]);
        var installs = AnswerInstallers();

        await using var signIn = await host.OpenSignInAsync(null, "foo", [], 80, 24, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(installs, i => i.Command.Any(c => c.StartsWith("agent=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Main_1")]
    [InlineData("-main")]
    [InlineData("main-")]
    [InlineData("a b")]
    public void An_instance_that_is_not_a_DNS_label_is_refused(string instance)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Options(instance));

        Assert.Contains("ILD_K8S_INSTANCE", ex.Message);
    }

    [Fact]
    public void An_instance_too_long_for_the_names_derived_from_it_is_refused()
        => Assert.Throws<InvalidOperationException>(() => Options(new string('a', 51)));

    [Fact]
    public async Task Closing_stdin_without_the_v5_protocol_is_an_error_and_not_a_silent_hang()
    {
        await _api.DisposeAsync();
        _api = await FakeKubernetesApi.StartAsync(offerV5: false);
        _host = NewHost(Options());
        var run = Guid.NewGuid();
        _api.Seed("pods", Name(run), Instance, run);
        _api.OnExec = async e => { await e.ReadStdinToEndAsync(); await e.ExitAsync(0); };

        using var exec = await PodExec.OpenAsync(_api.Client(), FakeKubernetesApi.Namespace, Name(run), ["cat"], stdin: true, tty: false, TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => exec.CloseStdinAsync(TestContext.Current.CancellationToken));

        Assert.Contains("v5.channel.k8s.io", ex.Message);
    }
}
