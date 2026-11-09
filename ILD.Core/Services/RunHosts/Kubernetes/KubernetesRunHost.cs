using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Tools;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Kubernetes;

/// <summary>
/// The run host that puts each loop run in a pod of its own, with a volume of its own. The run's Secret owns the pod,
/// the volume claim and the network policy, so deleting the Secret deletes them all.
/// </summary>
public sealed class KubernetesRunHost : ExecRunHost, ISignInTerminalHost, IRunHostShutdown
{
    public const string RunLeftoverKind = "kubernetes-run";

    private readonly IKubernetes _client;
    private readonly KubernetesRunHostOptions _options;
    private readonly string _namespace;
    private readonly KubernetesManifests _manifests;
    private readonly ConcurrentDictionary<Guid, string> _podAddresses = new();

    // What the store gave each set of toolchains and agents asked for, and the pods exec'd into that are not a run's.
    private readonly SemaphoreSlim _chatGate = new(1, 1);
    // One replacement of an ended run pod at a time, so concurrent commands on a dead pod make it once.
    private readonly SemaphoreSlim _runPodGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, string> _execNames = new();
    // The sign-in pods open now, which the sweep leaves alone.
    private readonly ConcurrentDictionary<string, byte> _signIns = new(StringComparer.Ordinal);

    public KubernetesRunHost(
        IKubernetes client, KubernetesRunHostOptions options, string namespaceName, IProcessRunner runner, ILogger? logger = null)
        : base(runner, logger)
    {
        _client = client;
        _options = options;
        _namespace = namespaceName;
        _manifests = new KubernetesManifests(options, namespaceName);
    }

    /// <summary>How often a starting pod is looked at.</summary>
    internal TimeSpan ReadyPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How ILD's name in <c>ILD_API_URL</c> is resolved for a pod that has no DNS of its own.</summary>
    internal Func<string, CancellationToken, Task<System.Net.IPAddress[]>> ResolveIld { get; init; } = (host, ct) => System.Net.Dns.GetHostAddressesAsync(host, ct);

    /// <summary>The first nameserver that is not a loopback one in ILD's own resolver configuration, which is the cluster's DNS; null when it has none. Replaced in tests.</summary>
    internal Func<string?> ClusterNameServer { get; init; } = () =>
    {
        try
        {
            return File.ReadLines("/etc/resolv.conf")
                .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[0] == "nameserver" && System.Net.IPAddress.TryParse(p[1], out var ip) && !System.Net.IPAddress.IsLoopback(ip))
                .Select(p => p[1])
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
    };

    private string? _nameServer;
    private bool _nameServerKnown;

    // What a run's or chat's pod is given for DNS: read once, since a pod made with another is replaced as stale.
    private string? NameServer()
    {
        if (!_nameServerKnown) { _nameServer = ClusterNameServer(); _nameServerKnown = true; }
        return _nameServer;
    }

    /// <summary>The agent CLIs a run gets: those ILD has installed, at their versions. Replaced in tests.</summary>
    internal Func<IReadOnlyList<RunAgent>> Agents { get; init; } = () => RunAgent.Installed(ManagedAgentInstall.ResolveDataRoot());

    public override RunHostKind Kind => RunHostKind.Kubernetes;

    public override string LeftoverKind => RunLeftoverKind;

    public override TimeSpan SweepInterval => _options.SweepInterval;

    protected override string Noun => "pod";

    public override bool HostsChats => true;

    protected override bool ChatToolchains => _options.UsesToolStore;

    protected override string IldUrl => RunFacingUrl(_options.IldUrl);

    protected override int EgressProxyPort => _options.EgressProxyPort;

    // -- Lifecycle ---------------------------------------------------------------

    public override async Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
    {
        var owner = request.OwnerId;
        var workingDirectory = RunPaths.WorkingDirectory(owner);
        if (await IsHealthyAsync(owner, ct))
            return new RunEnvironment(owner, workingDirectory);

        var tools = await ToolsForAsync($"Run {owner}", RunToolDetector.Resolve(request.BasePath), request.RepositoryId, ct);
        var (secret, madeSecret) = await CreateOwnerAsync(owner, ct);
        try
        {
            // A pod that ended (evicted, its node shut down) is replaced from the manifest as it is now, on the claim it had.
            await RemoveEndedRunPodAsync(KubernetesNames.ObjectName(owner), ct);
            var reference = KubernetesManifests.OwnerReference(secret);
            await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(_manifests.Claim(owner, reference), _namespace, cancellationToken: ct));
            await CreateIfMissingAsync(() => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(_manifests.Policy(owner, reference, request.Dns), _namespace, cancellationToken: ct));
            var ildAddress = request.Dns && RunFacingHost(new Uri(_options.IldUrl).Host) == new Uri(_options.IldUrl).Host ? null : await IldAddressAsync(ct);
            await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPodAsync(_manifests.Pod(owner, reference, tools, ildAddress, request.Dns, NameServer()), _namespace, cancellationToken: ct));
            var pod = await WaitReadyAsync(KubernetesNames.ObjectName(owner), $"run {owner:N}", ct);
            _podAddresses[owner] = pod.Status.PodIP;

            await PrepareAsync(owner, request, ct);
        }
        catch
        {
            // Only what this call made: a run that already had its objects keeps its volume through a failed retry.
            if (madeSecret) await DeleteOwnerQuietlyAsync(owner);
            throw;
        }

        return new RunEnvironment(owner, workingDirectory);
    }

    public override async Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
    {
        await SyncPreviewDataBackAsync(environment, ct);
        _podAddresses.TryRemove(environment.OwnerId, out _);
        await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedSecretAsync(
            KubernetesNames.ObjectName(environment.OwnerId), _namespace, new V1DeleteOptions { PropagationPolicy = "Background" }, cancellationToken: ct));
    }

    protected override Task<bool> IsRunningAsync(Guid owner, CancellationToken ct) => IsHealthyAsync(owner, ct);

    private async Task<bool> IsHealthyAsync(Guid owner, CancellationToken ct)
    {
        try
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
            if (!IsReady(pod)) return false;
            _podAddresses[owner] = pod.Status.PodIP;
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<(V1Secret Secret, bool Made)> CreateOwnerAsync(Guid owner, CancellationToken ct)
    {
        try
        {
            return (await _client.CoreV1.CreateNamespacedSecretAsync(_manifests.Secret(owner), _namespace, cancellationToken: ct), true);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return (await _client.CoreV1.ReadNamespacedSecretAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct), false);
        }
    }

    /// <summary>Deletes the pod when it has ended or is going away, and waits until it is gone, so a new one can take its name.</summary>
    private async Task RemoveEndedRunPodAsync(string name, CancellationToken ct)
    {
        var pod = await ReadPodOrNullAsync(name, ct);
        if (pod is null || pod.Metadata.DeletionTimestamp is null && pod.Status?.Phase is not ("Failed" or "Succeeded")) return;
        await DeletePodAndWaitAsync(name, ct);
    }

    private async Task DeletePodAndWaitAsync(string name, CancellationToken ct)
    {
        // Also when it is already terminating: on a lost node the kubelet never confirms the delete, and only a grace period of 0 removes the pod.
        await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(name, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: ct));
        await WaitPodGoneAsync(name, ct);
    }

    /// <summary>
    /// Makes an ended run pod again from its own record (labels, owner and spec, so it keeps the claim and the tool
    /// mounts it had), and waits until it is ready. False when the pod is not an ended one, which is not for this to mend.
    /// </summary>
    private async Task<bool> ReplaceEndedRunPodAsync(Guid owner, CancellationToken ct)
    {
        var name = KubernetesNames.ObjectName(owner);
        await _runPodGate.WaitAsync(ct);
        try
        {
            var pod = await ReadPodOrNullAsync(name, ct);
            if (pod is null) return false;
            if (IsReady(pod) && pod.Metadata.DeletionTimestamp is null) return true;
            if (pod.Metadata.DeletionTimestamp is null && pod.Status?.Phase is not ("Failed" or "Succeeded")) return false;

            Logger?.LogInformation("The pod of run {Run} ended ({Why}); making it again on its volume", owner, WhyNotReady(pod));
            await DeletePodAndWaitAsync(name, ct);
            var fresh = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    NamespaceProperty = _namespace,
                    Labels = pod.Metadata.Labels,
                    Annotations = pod.Metadata.Annotations,
                    OwnerReferences = pod.Metadata.OwnerReferences,
                },
                Spec = pod.Spec,
            };
            fresh.Spec.NodeName = null;
            await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPodAsync(fresh, _namespace, cancellationToken: ct));
            var ready = await WaitReadyAsync(name, $"run {owner:N}", ct);
            _podAddresses[owner] = ready.Status.PodIP;
            return true;
        }
        finally
        {
            _runPodGate.Release();
        }
    }

    private static async Task CreateIfMissingAsync<T>(Func<Task<T>> create)
    {
        try { await create(); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict) { }
    }

    private static async Task IgnoreNotFoundAsync<T>(Func<Task<T>> call)
    {
        try { await call(); }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
    }

    private async Task DeleteOwnerQuietlyAsync(Guid owner)
    {
        try { await DeleteAsync(new RunEnvironment(owner, RunPaths.WorkingDirectory(owner)), CancellationToken.None); }
        catch (Exception ex) { Logger?.LogWarning(ex, "Could not remove the objects of run {Run} after its pod failed to start; the next sweep removes them", owner); }
    }

    private static bool IsReady(V1Pod pod)
        => pod.Status?.Phase == "Running" && !string.IsNullOrEmpty(pod.Status.PodIP)
            && pod.Status.ContainerStatuses?.All(c => c.Ready) == true;

    private async Task<V1Pod> WaitReadyAsync(string name, string subject, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(name, _namespace, cancellationToken: ct);
            if (IsReady(pod)) return pod;
            if (pod.Status?.Phase is "Failed" or "Succeeded")
                throw new InvalidOperationException($"the pod of {subject} ended before it was ready: {WhyNotReady(pod)}");
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the pod of {subject} was not ready after {_options.ReadyTimeout.TotalSeconds:0} s: {WhyNotReady(pod)}");
            await Task.Delay(ReadyPollInterval, ct);
        }
    }

    private static string WhyNotReady(V1Pod pod)
    {
        var condition = pod.Status?.Conditions?.FirstOrDefault(c => c.Status == "False" && !string.IsNullOrEmpty(c.Message));
        return condition?.Message ?? pod.Status?.Reason ?? pod.Status?.Phase ?? "no status";
    }

    public override async Task CheckAsync(CancellationToken ct = default)
    {
        await CheckStorageAsync(ct);
        try
        {
            await RetireStaleChatPodAsync(ct);
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
        {
            Logger?.LogWarning(ex, "Could not check the chat pod; it is checked again when a chat next needs it");
        }
        if (_options.Toolchains && _options.ToolsClaim is null)
            Logger?.LogWarning(
                "{Claim} is not set, so runs get no toolchains or agents from the store and have only what the runner image {Image} has. " +
                "Set it to a ReadWriteMany claim for the store, or use a runner image of your own that has them, with {Toolchains}=off",
                KubernetesRunHostOptions.ToolsClaimSetting, _options.RunnerImage, RunHostOptions.ToolchainsSetting);
    }

    // -- Toolchains --------------------------------------------------------------

    /// <summary>
    /// What the run's commands get from the toolchain store, each zone installed into it first when it lacks them; null
    /// when the runs mount no store (toolchains off, or no claim to hold it), so the run has only what the runner image has.
    /// </summary>
    protected override async Task<RunToolEnvironment?> ToolsForAsync(string label, RunToolPlan plan, Guid? repositoryId, CancellationToken ct)
        => _options.UsesToolStore ? await ToolZonesAsync(label, plan, Agents(), repositoryId, ct) : null;

    protected override string ToolZoneName(RunToolMount zone) => $"{_options.ToolsClaim}/{zone.SubPath}";

    protected override async Task<T> WithToolInstallerAsync<T>(ToolInstaller toolInstaller, Func<Guid, Task<T>> work, CancellationToken ct)
    {
        var installer = Guid.NewGuid();
        var name = KubernetesNames.ToolsName(installer);
        _execNames[installer] = name;
        try
        {
            // The policy first, so the pod never runs a moment without it.
            await _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(_manifests.ToolsPolicy(installer), _namespace, cancellationToken: ct);
            await _client.CoreV1.CreateNamespacedPodAsync(
                _manifests.ToolsInstaller(installer, toolInstaller.Writable, toolInstaller.ReadOnly, toolInstaller.Environment, await IldAddressAsync(ct)), _namespace, cancellationToken: ct);
            await WaitReadyAsync(name, "the toolchain installer", ct);
            return await work(installer);
        }
        finally
        {
            _execNames.TryRemove(installer, out _);
            await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(
                name, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: CancellationToken.None));
            await IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(name, _namespace, cancellationToken: CancellationToken.None));
        }
    }

    // -- Provider sign-in --------------------------------------------------------

    /// <summary>
    /// A terminal for a provider's login in a short-lived pod of the runner image of its own: the agent from the toolchain
    /// store (installed first if it is missing), no NetworkPolicy so the OAuth flow can reach the provider, and none of
    /// what a chat or run has. It writes the credentials the model gateway keeps from agents, so nothing else shares it.
    /// The pod goes when the result is disposed, or on a failure to start.
    /// </summary>
    public async Task<SignInTerminal> OpenSignInAsync(ManagedAgent? agent, string command, IReadOnlyList<string> arguments, int cols, int rows, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var name = KubernetesNames.SignInName(id);
        _signIns[name] = 0;
        _execNames[id] = name;

        async Task RemoveAsync()
        {
            _execNames.TryRemove(id, out _);
            // Forgotten whether or not the delete works, so the sweep can remove a pod that it did not.
            _signIns.TryRemove(name, out _);
            try
            {
                await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(
                    name, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: CancellationToken.None));
            }
            catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
            {
                Logger?.LogWarning(ex, "Could not remove the sign-in pod {Name}; the next sweep removes it", name);
            }
        }

        try
        {
            // Only the agent being signed in, as the container host does; none when there is none to install.
            var agents = agent is null ? (IReadOnlyList<RunAgent>)[] : Agents().Where(a => a.Agent.Key == agent.Key).ToList();
            var tools = _options.UsesToolStore ? await ToolZonesAsync("The provider sign-in", new RunToolPlan([], []), agents, null, ct) : null;
            await _client.CoreV1.CreateNamespacedPodAsync(
                _manifests.SignIn(name, SignInHome, tools?.For(SignInHome) ?? [], tools?.Zones ?? []), _namespace, cancellationToken: ct);
            await WaitReadyAsync(name, "the provider sign-in", ct);

            var exec = await OpenExecAsync(id, SignInTerminalCommand(command, arguments), stdin: true, tty: true, ct);
            var pty = new ExecPty(exec);
            pty.Resize(cols, rows);
            return new SignInTerminal(pty, (path, token) => ReadSignInFileAsync(id, path, token), RemoveAsync);
        }
        catch
        {
            await RemoveAsync();
            throw;
        }
    }

    // -- Preview -----------------------------------------------------------------

    public override async Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
    {
        var target = OwnerFor(workingDirectory);
        if (target.Chat) throw new NotSupportedException("a service in a chat cannot be previewed");
        var owner = target.Id;
        if (!_podAddresses.TryGetValue(owner, out var address))
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
            address = pod.Status?.PodIP is { Length: > 0 } ip ? ip : throw new InvalidOperationException($"the pod of run {owner:N} has no address yet");
            _podAddresses[owner] = address;
        }
        return new UriBuilder(Uri.UriSchemeHttp, address, port).Uri;
    }

    protected override async Task<bool> RunExistsAsync(ExecOwner owner, CancellationToken ct)
    {
        // A chat pod that is not there has nothing running to kill, and asking would make it.
        try
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(owner.Chat ? ChatPodName : KubernetesNames.ObjectName(owner.Id), _namespace, cancellationToken: ct);
            return !owner.Chat || IsReady(pod);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    protected override async Task<IExecStream> OpenExecStreamAsync(ExecOwner owner, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
    {
        if (owner.Chat)
            return await PodExec.OpenAsync(_client, _namespace, await EnsureChatPodAsync(ct), command, stdin, tty, ct);
        if (_execNames.TryGetValue(owner.Id, out var other))
            return await PodExec.OpenAsync(_client, _namespace, other, command, stdin, tty, ct);

        var name = KubernetesNames.ObjectName(owner.Id);
        try
        {
            return await PodExec.OpenAsync(_client, _namespace, name, command, stdin, tty, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A pod nothing restarts (RestartPolicy Never) that was evicted or lost its node: made again once, then the command retried.
            if (!await ReplaceEndedRunPodAsync(owner.Id, ct)) throw;
            return await PodExec.OpenAsync(_client, _namespace, name, command, stdin, tty, ct);
        }
    }

    // -- Direct route (network mode Off) ------------------------------------------

    private string PolicyOf(ExecOwner owner) => KubernetesNames.OpenName(owner.Chat ? ChatPodName : KubernetesNames.ObjectName(owner.Id));

    protected override async Task OpenDirectRouteAsync(ExecOwner owner, CancellationToken ct)
    {
        if (owner.Chat)
        {
            await EnsureChatPodAsync(ct);
            await CreateIfMissingAsync(() => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(_manifests.ChatOpenPolicy(), _namespace, cancellationToken: ct));
            return;
        }
        var secret = await _client.CoreV1.ReadNamespacedSecretAsync(KubernetesNames.ObjectName(owner.Id), _namespace, cancellationToken: ct);
        await CreateIfMissingAsync(() => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
            _manifests.OpenPolicy(owner.Id, KubernetesManifests.OwnerReference(secret)), _namespace, cancellationToken: ct));
    }

    protected override Task CloseDirectRouteAsync(ExecOwner owner, CancellationToken ct)
        => IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(PolicyOf(owner), _namespace, cancellationToken: ct));

    protected override async Task<bool> HasDirectRouteAsync(ExecOwner owner, CancellationToken ct)
    {
        try
        {
            await _client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(PolicyOf(owner), _namespace, cancellationToken: ct);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public override async Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default)
    {
        var policies = await _client.NetworkingV1.ListNamespacedNetworkPolicyAsync(
            _namespace, labelSelector: $"{KubernetesNames.SelectorFor(_options.Instance)},{RunPaths.RoleLabel}={RunPaths.OpenRole}", cancellationToken: ct);
        return policies.Items
            .Select(p => p.Metadata.Labels.TryGetValue(RunPaths.RunLabel, out var run) && Guid.TryParseExact(run, "N", out var id)
                ? new DirectRoute(id, Chat: false)
                : new DirectRoute(Guid.Empty, Chat: true))
            .ToList();
    }

    // -- Chats -------------------------------------------------------------------

    private string ChatPodName => KubernetesNames.ChatName(_options.Instance);

    /// <summary>
    /// The name of the chat pod, made first when it is missing, and replaced when it has ended or is going away. The claim
    /// keeps the chats' folders through that.
    /// </summary>
    private async Task<string> EnsureChatPodAsync(CancellationToken ct)
    {
        await ChatIldAddressAsync(ct);
        if (!ChatBeingRemoved && await ChatPodStateAsync(ct) is { } pod && IsReady(pod) && pod.Metadata.DeletionTimestamp is null && IsCurrent(pod)) return ChatPodName;

        await _chatGate.WaitAsync(ct);
        try
        {
            var existing = await ChatPodStateAsync(ct);
            if (existing is not null && IsReady(existing) && existing.Metadata.DeletionTimestamp is null && IsCurrent(existing)) return ChatPodName;

            if (existing is not null && (existing.Metadata.DeletionTimestamp is not null || existing.Status?.Phase is "Failed" or "Succeeded" || !IsCurrent(existing)))
            {
                if (existing.Metadata.DeletionTimestamp is null)
                    await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(ChatPodName, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: ct));
                await WaitPodGoneAsync(ChatPodName, ct);
                existing = null;
            }

            if (existing is null)
            {
                await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(_manifests.ChatClaim(), _namespace, cancellationToken: ct));
                // A new pod starts with no direct route, whatever the one before it had.
                await IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(KubernetesNames.OpenName(ChatPodName), _namespace, cancellationToken: ct));
                await CreateIfMissingAsync(() => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(_manifests.ChatPolicy(), _namespace, cancellationToken: ct));
                await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPodAsync(_manifests.ChatPod(_chatIldAddress, NameServer()), _namespace, cancellationToken: ct));
            }
            await WaitReadyAsync(ChatPodName, "the chats", ct);
            if (TakeRemovedForIdle()) Logger?.LogInformation("The chat pod {Name} is back, made for a chat after it was deleted for idleness", ChatPodName);
            return ChatPodName;
        }
        finally
        {
            _chatGate.Release();
        }
    }

    private string? _chatSpec;
    private string? _chatIldAddress;
    private bool _chatIldAddressKnown;

    private async Task ChatIldAddressAsync(CancellationToken ct)
    {
        if (_chatIldAddressKnown) return;
        _chatIldAddress = await IldAddressAsync(ct);
        _chatIldAddressKnown = true;
    }

    /// <summary>
    /// ILD's Service address, which a pod without DNS is given for ILD's name; null when ILD is named by address. A Service that
    /// is recreated gets a new address, so a pod made before that must be replaced.
    /// </summary>
    private async Task<string?> IldAddressAsync(CancellationToken ct)
    {
        var host = new Uri(_options.IldUrl).Host;
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _)) return null;
        try
        {
            var addresses = await ResolveIld(host, ct);
            return (addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.First()).ToString();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            throw new InvalidOperationException($"ILD_API_URL names {host}, which ILD cannot resolve, so a pod without DNS cannot be given its address ({ex.Message})", ex);
        }
    }

    // Whether the chat pod was made from the spec ILD would make it from now.
    private bool IsCurrent(V1Pod pod)
        => pod.Metadata.Annotations?.TryGetValue(KubernetesNames.SpecAnnotation, out var spec) == true
            && spec == (_chatSpec ??= KubernetesManifests.SpecOf(_manifests.ChatPod(_chatIldAddress, NameServer())));

    /// <summary>Deletes a chat pod made from another runner image or other settings, so it is not left running until the next chat.</summary>
    internal async Task RetireStaleChatPodAsync(CancellationToken ct)
    {
        await ChatIldAddressAsync(ct);
        if (await ChatPodStateAsync(ct) is not { } pod || pod.Metadata.DeletionTimestamp is not null || IsCurrent(pod)) return;
        Logger?.LogInformation("Replacing the chat pod: the runner image or ILD's settings changed since it was made");
        await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(ChatPodName, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: ct));
    }

    // -- Shutdown ----------------------------------------------------------------

    /// <summary>
    /// What ILD puts away when it stops, after the drain. The chat pod and any sign-in or installer pod are deleted: the chats' folders
    /// are in their claim, and the next chat makes the pod from the runner image as it is then. A run's pod stays: a
    /// pod cannot be stopped, and it is what a parked run resumes in.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        var options = new V1DeleteOptions { GracePeriodSeconds = 0 };
        await Task.WhenAll(new[] { ChatPodName }.Concat(_signIns.Keys).Concat(_execNames.Values.Where(n => n.StartsWith("ild-tools-", StringComparison.Ordinal))).Distinct().Select(async name =>
        {
            try
            {
                await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(name, _namespace, options, cancellationToken: ct));
            }
            catch (Exception ex) when (ex is HttpOperationException or HttpRequestException or OperationCanceledException)
            {
                Logger?.LogWarning(ex, "Could not delete the pod {Name}; it is replaced or swept when ILD next starts", name);
            }
        }));
    }

    protected override TimeSpan ChatIdleTimeout => _options.ChatIdleTimeout;

    // By its name, never by what ILD manages: a run's pod, parked or waiting for a human, is not the chat's to reap.
    public override Task ReapIdleChatAsync(CancellationToken ct = default)
        => ReapChatAsync(_chatGate, ChatPodName, token =>
            IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(ChatPodName, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: token)), ct);

    private Task<V1Pod?> ChatPodStateAsync(CancellationToken ct) => ReadPodOrNullAsync(ChatPodName, ct);

    private async Task<V1Pod?> ReadPodOrNullAsync(string name, CancellationToken ct)
    {
        try
        {
            return await _client.CoreV1.ReadNamespacedPodAsync(name, _namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task WaitPodGoneAsync(string name, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (await ReadPodOrNullAsync(name, ct) is not null)
        {
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the old pod {name} was still there after {_options.ReadyTimeout.TotalSeconds:0} s");
            await Task.Delay(ReadyPollInterval, ct);
        }
    }

    // -- Sweep -------------------------------------------------------------------

    public override async Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
    {
        try
        {
            var retained = await RetainedRuns(ct);
            var selector = KubernetesNames.SelectorFor(_options.Instance);
            var runs = new HashSet<string>(StringComparer.Ordinal);
            void Collect(IEnumerable<IMetadata<V1ObjectMeta>> items)
            {
                foreach (var item in items)
                    if (item.Metadata.Labels is { } labels && labels.TryGetValue(RunPaths.RunLabel, out var run))
                        runs.Add(run);
            }

            Collect((await _client.CoreV1.ListNamespacedSecretAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.NetworkingV1.ListNamespacedNetworkPolicyAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            // Sign-in and installer pods carry no run label; those ILD has not got open are left by a crash.
            var open = _execNames.Values.ToHashSet(StringComparer.Ordinal);
            var signIns = new List<string?>();
            foreach (var role in new[] { KubernetesNames.SignInRole, RunPaths.ToolsRole })
                signIns.AddRange((await _client.CoreV1.ListNamespacedPodAsync(
                        _namespace, labelSelector: $"{selector},{RunPaths.RoleLabel}={role}", cancellationToken: ct)).Items
                    .Select(p => p.Metadata.Name)
                    .Where(n => n is not null && !_signIns.ContainsKey(n) && !open.Contains(n)));
            // An installer's NetworkPolicy goes with its pod; one left without a pod is a leftover of its own.
            signIns.AddRange((await _client.NetworkingV1.ListNamespacedNetworkPolicyAsync(
                    _namespace, labelSelector: $"{selector},{RunPaths.RoleLabel}={RunPaths.ToolsRole}", cancellationToken: ct)).Items
                .Select(p => p.Metadata.Name)
                .Where(n => n is not null && !open.Contains(n) && !signIns.Contains(n)));

            // The chat's direct route belongs to its pod, which has no owner reference to take the policy with it.
            if ((await _client.NetworkingV1.ListNamespacedNetworkPolicyAsync(_namespace, labelSelector: $"{selector},{RunPaths.RoleLabel}={RunPaths.OpenRole}", cancellationToken: ct)).Items
                    .Any(p => p.Metadata.Name == KubernetesNames.OpenName(ChatPodName))
                && await ChatPodStateAsync(ct) is null)
                signIns.Add(KubernetesNames.OpenName(ChatPodName));

            return runs
                .Where(run => !Guid.TryParseExact(run, "N", out var id) || !liveIds.Contains(id) && !retained.Contains(id))
                .Select(run => new RunLeftover(RunLeftoverKind, run))
                .Concat(signIns.Select(n => new RunLeftover(RunLeftoverKind, n!)))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
        {
            Logger?.LogWarning(ex, "Could not list the pods runs left in namespace {Namespace}; the next sweep tries again", _namespace);
            return [];
        }
    }

    public override async Task<bool> DeleteLeftoverAsync(RunLeftover leftover, CancellationToken ct = default)
    {
        if (leftover.Kind != RunLeftoverKind)
            throw new ArgumentException($"unknown leftover kind '{leftover.Kind}'", nameof(leftover));
        if (leftover.Location.StartsWith("ild-signin-", StringComparison.Ordinal) || leftover.Location.StartsWith("ild-tools-", StringComparison.Ordinal))
        {
            try
            {
                await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(
                    leftover.Location, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: ct));
                if (leftover.Location.StartsWith("ild-tools-", StringComparison.Ordinal))
                    await IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(leftover.Location, _namespace, cancellationToken: ct));
                return true;
            }
            catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
            {
                Logger?.LogWarning(ex, "Could not delete the sign-in or installer pod {Name}; the next sweep tries again", leftover.Location);
                return false;
            }
        }
        if (leftover.Location == KubernetesNames.OpenName(ChatPodName))
        {
            try
            {
                await IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(leftover.Location, _namespace, cancellationToken: ct));
                return true;
            }
            catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
            {
                Logger?.LogWarning(ex, "Could not delete the chat pod's open NetworkPolicy {Name}; the next sweep tries again", leftover.Location);
                return false;
            }
        }
        if (!Guid.TryParseExact(leftover.Location, "N", out var owner))
            return false;

        var name = KubernetesNames.ObjectName(owner);
        var options = new V1DeleteOptions { PropagationPolicy = "Background" };
        try
        {
            // The Secret first, which takes the rest with it; each is also deleted by name for a leftover whose Secret is already gone.
            await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedSecretAsync(name, _namespace, options, cancellationToken: ct));
            await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(name, _namespace, options, cancellationToken: ct));
            await IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(name, _namespace, options, cancellationToken: ct));
            await IgnoreNotFoundAsync(() => _client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(KubernetesNames.OpenName(name), _namespace, options, cancellationToken: ct));
            await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(name, _namespace, options, cancellationToken: ct));
            _podAddresses.TryRemove(owner, out _);
            return true;
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
        {
            Logger?.LogWarning(ex, "Could not delete the objects of run {Run}; the next sweep tries again", owner);
            return false;
        }
    }

    /// <summary>
    /// Warns when the volumes of runs would outlive them: a StorageClass whose reclaim policy is not <c>Delete</c>
    /// keeps the disk after its claim is gone. Says so rather than failing when the class cannot be read.
    /// </summary>
    public async Task CheckStorageAsync(CancellationToken ct = default)
    {
        try
        {
            var classes = (await _client.StorageV1.ListStorageClassAsync(cancellationToken: ct)).Items;
            var chosen = _options.StorageClass is { } name
                ? classes.FirstOrDefault(c => c.Metadata.Name == name)
                : classes.FirstOrDefault(c => c.Metadata.Annotations?.TryGetValue("storageclass.kubernetes.io/is-default-class", out var isDefault) == true && isDefault == "true");
            if (chosen is null)
            {
                Logger?.LogWarning("There is no {Class} StorageClass, so the volume of a run cannot be provisioned", _options.StorageClass ?? "default");
                return;
            }
            if (!string.Equals(chosen.ReclaimPolicy ?? "Delete", "Delete", StringComparison.Ordinal))
                Logger?.LogWarning(
                    "StorageClass {Class} has reclaimPolicy {Policy}: the volume of a run is kept after the run is removed. Use a class with reclaimPolicy Delete for runs",
                    chosen.Metadata.Name, chosen.ReclaimPolicy);
        }
        catch (Exception ex) when (ex is HttpOperationException or HttpRequestException)
        {
            Logger?.LogInformation("Could not read the StorageClasses to check their reclaim policy ({Reason}); it needs read access to storageclasses", ex.Message);
        }
    }

}
