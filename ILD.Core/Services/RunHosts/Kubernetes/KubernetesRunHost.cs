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
public sealed class KubernetesRunHost : ExecRunHost
{
    public const string RunLeftoverKind = "kubernetes-run";

    private readonly IKubernetes _client;
    private readonly KubernetesRunHostOptions _options;
    private readonly string _namespace;
    private readonly KubernetesManifests _manifests;
    private readonly ConcurrentDictionary<Guid, string> _podAddresses = new();

    // What the store gave each set of toolchains and agents asked for, and the pods exec'd into that are not a run's.
    private readonly ConcurrentDictionary<string, RunToolEnvironment> _tools = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _toolsGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, string> _execNames = new();

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

    /// <summary>The agent CLIs a run gets: those ILD has installed, at their versions. Replaced in tests.</summary>
    internal Func<IReadOnlyList<RunAgent>> Agents { get; init; } = () => RunAgent.Installed(ManagedAgentInstall.ResolveDataRoot());

    public override RunHostKind Kind => RunHostKind.Kubernetes;

    public override string LeftoverKind => RunLeftoverKind;

    public override TimeSpan SweepInterval => _options.SweepInterval;

    protected override string Noun => "pod";

    protected override string IldUrl => _options.IldUrl;

    protected override int EgressProxyPort => _options.EgressProxyPort;

    // -- Lifecycle ---------------------------------------------------------------

    public override async Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
    {
        var owner = request.OwnerId;
        var workingDirectory = KubernetesNames.WorkingDirectory(owner);
        if (await IsHealthyAsync(owner, ct))
            return new RunEnvironment(owner, workingDirectory);

        var tools = await ToolsForAsync(request, ct);
        var secret = await CreateOwnerAsync(owner, ct);
        try
        {
            var reference = KubernetesManifests.OwnerReference(secret);
            await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(_manifests.Claim(owner, reference), _namespace, cancellationToken: ct));
            await CreateIfMissingAsync(() => _client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(_manifests.Policy(owner, reference), _namespace, cancellationToken: ct));
            await CreateIfMissingAsync(() => _client.CoreV1.CreateNamespacedPodAsync(_manifests.Pod(owner, reference, tools), _namespace, cancellationToken: ct));
            var pod = await WaitReadyAsync(owner, ct);
            _podAddresses[owner] = pod.Status.PodIP;

            await PrepareAsync(owner, request, ct);
        }
        catch
        {
            await DeleteOwnerQuietlyAsync(owner);
            throw;
        }

        return new RunEnvironment(owner, workingDirectory);
    }

    public override async Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
    {
        _podAddresses.TryRemove(environment.OwnerId, out _);
        await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedSecretAsync(
            KubernetesNames.ObjectName(environment.OwnerId), _namespace, new V1DeleteOptions { PropagationPolicy = "Background" }, cancellationToken: ct));
    }

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

    private async Task<V1Secret> CreateOwnerAsync(Guid owner, CancellationToken ct)
    {
        try
        {
            return await _client.CoreV1.CreateNamespacedSecretAsync(_manifests.Secret(owner), _namespace, cancellationToken: ct);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return await _client.CoreV1.ReadNamespacedSecretAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
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
        try { await DeleteAsync(new RunEnvironment(owner, KubernetesNames.WorkingDirectory(owner)), CancellationToken.None); }
        catch (Exception ex) { Logger?.LogWarning(ex, "Could not remove the objects of run {Run} after its pod failed to start; the next sweep removes them", owner); }
    }

    private static bool IsReady(V1Pod pod)
        => pod.Status?.Phase == "Running" && !string.IsNullOrEmpty(pod.Status.PodIP)
            && pod.Status.ContainerStatuses?.All(c => c.Ready) == true;

    private async Task<V1Pod> WaitReadyAsync(Guid owner, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner), _namespace, cancellationToken: ct);
            if (IsReady(pod)) return pod;
            if (pod.Status?.Phase is "Failed" or "Succeeded")
                throw new InvalidOperationException($"the pod of run {owner:N} ended before it was ready: {WhyNotReady(pod)}");
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the pod of run {owner:N} was not ready after {_options.ReadyTimeout.TotalSeconds:0} s: {WhyNotReady(pod)}");
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
        if (_options.Toolchains && _options.ToolsClaim is null)
            Logger?.LogWarning(
                "{Claim} is not set, so runs get no toolchains or agents from the store and have only what the runner image {Image} has. " +
                "Set it to a ReadWriteMany claim for the store, or run the full runner image (ild-runner-full) with {Toolchains}=off",
                KubernetesRunHostOptions.ToolsClaimSetting, _options.RunnerImage, RunHostOptions.ToolchainsSetting);
    }

    // -- Toolchains --------------------------------------------------------------

    /// <summary>
    /// What the run's commands get from the toolchain store, installed into it first when it lacks them; null when the
    /// runs mount no store (toolchains off, or no claim to hold it), so the run has only what the runner image has.
    /// </summary>
    private async Task<RunToolEnvironment?> ToolsForAsync(RunEnvironmentRequest request, CancellationToken ct)
    {
        if (!_options.UsesToolStore) return null;

        var plan = RunToolDetector.Resolve(request.BasePath);
        var agents = Agents();
        var key = string.Join('\n', RunToolStore.Arguments(plan.Tools, agents));
        if (key.Length == 0) return RunToolEnvironment.None;
        if (_tools.TryGetValue(key, out var known)) return known;

        await _toolsGate.WaitAsync(ct);
        try
        {
            if (_tools.TryGetValue(key, out known)) return known;
            Logger?.LogInformation("Run {Run} gets {Plan}, and the agents {Agents}, from the toolchain store {Claim}",
                request.OwnerId, plan, string.Join(", ", agents.Select(a => $"{a.Agent.Key}@{a.Version}")), _options.ToolsClaim);

            var installer = Guid.NewGuid();
            var name = $"ild-tools-{installer:N}";
            _execNames[installer] = name;
            try
            {
                await _client.CoreV1.CreateNamespacedPodAsync(_manifests.ToolsInstaller(name), _namespace, cancellationToken: ct);
                await WaitInstallerReadyAsync(name, ct);
                var environment = await InstallToolsAsync(installer, plan.Tools, agents, ct);
                Logger?.LogInformation("Run {Run} has {Installed}", request.OwnerId, string.Join(", ", environment.Installed));
                return _tools[key] = environment;
            }
            finally
            {
                _execNames.TryRemove(installer, out _);
                await IgnoreNotFoundAsync(() => _client.CoreV1.DeleteNamespacedPodAsync(
                    name, _namespace, new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: CancellationToken.None));
            }
        }
        finally
        {
            _toolsGate.Release();
        }
    }

    private async Task WaitInstallerReadyAsync(string name, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var pod = await _client.CoreV1.ReadNamespacedPodAsync(name, _namespace, cancellationToken: ct);
            if (IsReady(pod)) return;
            if (pod.Status?.Phase is "Failed" or "Succeeded")
                throw new InvalidOperationException($"the toolchain installer {name} ended before it was ready: {WhyNotReady(pod)}");
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the toolchain installer {name} was not ready after {_options.ReadyTimeout.TotalSeconds:0} s: {WhyNotReady(pod)}");
            await Task.Delay(ReadyPollInterval, ct);
        }
    }

    // -- Preview -----------------------------------------------------------------

    public override async Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
    {
        var owner = OwnerFor(workingDirectory).Id;
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
        if (owner.Chat) return false;
        try
        {
            await _client.CoreV1.ReadNamespacedPodAsync(KubernetesNames.ObjectName(owner.Id), _namespace, cancellationToken: ct);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    protected override async Task<IExecStream> OpenExecStreamAsync(ExecOwner owner, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
        => await PodExec.OpenAsync(_client, _namespace, _execNames.TryGetValue(owner.Id, out var other) ? other : KubernetesNames.ObjectName(owner.Id), command, stdin, tty, ct);

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
                    if (item.Metadata.Labels is { } labels && labels.TryGetValue(KubernetesNames.RunLabel, out var run))
                        runs.Add(run);
            }

            Collect((await _client.CoreV1.ListNamespacedSecretAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.CoreV1.ListNamespacedPodAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);
            Collect((await _client.NetworkingV1.ListNamespacedNetworkPolicyAsync(_namespace, labelSelector: selector, cancellationToken: ct)).Items);

            return runs
                .Where(run => !Guid.TryParseExact(run, "N", out var id) || !liveIds.Contains(id) && !retained.Contains(id))
                .Select(run => new RunLeftover(RunLeftoverKind, run))
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
