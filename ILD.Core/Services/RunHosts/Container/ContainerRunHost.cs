using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Tools;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.RunHosts.Container;

/// <summary>
/// The run host that puts each loop run in a container of its own, on the machine ILD runs on, through the
/// Docker-compatible API that Docker and Podman both serve. A run has a named volume for its files and a network of
/// its own that reaches only ILD, and all three carry the run's labels. There are no owner references: ending the
/// run deletes the container, the volume and the network, and the sweeper deletes what a killed ILD left.
/// </summary>
public sealed class ContainerRunHost : ExecRunHost, ISignInTerminalHost, IRunHostShutdown, IDisposable
{
    public const string RunLeftoverKind = "container-run";

    internal const uint RunnerUid = 10001;

    private readonly ContainerRunHostOptions _options;
    private readonly DockerApiClient _api;
    private readonly ConcurrentDictionary<Guid, string> _addresses = new();
    private string? _warning;
    private bool? _podman;

    private readonly SemaphoreSlim _chatGate = new(1, 1);
    // Containers that are not a run's, exec'd into by an id of their own: the store's installers.
    private readonly ConcurrentDictionary<Guid, string> _execNames = new();
    // The sign-in terminals open now, which the sweep must not take for leftovers.
    private readonly ConcurrentDictionary<string, byte> _signIns = new(StringComparer.Ordinal);

    public ContainerRunHost(ContainerRunHostOptions options, IProcessRunner runner, ILogger? logger = null)
        : base(runner, logger)
    {
        _options = options;
        _api = new DockerApiClient(options.Socket);
    }

    /// <summary>The agent CLIs a run gets: those ILD has installed, at their versions. Replaced in tests.</summary>
    internal Func<IReadOnlyList<RunAgent>> Agents { get; init; } = () => RunAgent.Installed(ManagedAgentInstall.ResolveDataRoot());

    /// <summary>How often a starting container is looked at.</summary>
    internal TimeSpan ReadyPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a command whose stream ended may take to report how it ended before it counts as cut off.</summary>
    internal TimeSpan ExitStatusTimeout { get; init; } = DockerExec.DefaultStatusTimeout;

    public override RunHostKind Kind => RunHostKind.Container;

    public override string LeftoverKind => RunLeftoverKind;

    public override TimeSpan SweepInterval => _options.SweepInterval;

    protected override string Noun => "container";

    protected override string IldUrl => RunFacingUrl(_options.IldUrl);

    protected override int EgressProxyPort => _options.EgressProxyPort;

    /// <summary>What a run's container, volume and network are called.</summary>
    public static string ObjectName(Guid ownerId) => $"ild-run-{ownerId:N}";

    private static string Escaped(string name) => Uri.EscapeDataString(name);

    private IDictionary<string, string> Labels(Guid ownerId) => new Dictionary<string, string>
    {
        [RunPaths.ManagedByLabel] = RunPaths.ManagedByValue,
        [RunPaths.InstanceLabel] = _options.Instance,
        [RunPaths.RunLabel] = ownerId.ToString("N"),
    };

    // -- Lifecycle ---------------------------------------------------------------

    public override async Task<RunEnvironment> CreateAsync(RunEnvironmentRequest request, CancellationToken ct = default)
    {
        var owner = request.OwnerId;
        var workingDirectory = RunPaths.WorkingDirectory(owner);
        if (await IsHealthyAsync(owner, ct))
            return new RunEnvironment(owner, workingDirectory);

        // Only what this call made is removed when it fails: an environment a retry reached keeps its run's checkout.
        var made = new Made();
        try
        {
            var name = ObjectName(owner);
            var tools = await ToolsForAsync($"Run {owner}", RunToolDetector.Resolve(request.BasePath), request.RepositoryId, ct);
            made.Volume = await TolerateConflictAsync(() => _api.PostAsync("/volumes/create", new { Name = name, Labels = Labels(owner) }, ct));
            made.Network = await TolerateConflictAsync(() => CreateInternalNetworkAsync(name, Labels(owner), ct));
            var ild = await IldAddressAsync(name, $"run {owner:N}", ct);
            made.Container = await CreateContainerAsync(owner, ild, tools, request.Dns, ct);
            await _api.PostAsync($"/containers/{Escaped(name)}/start", null, ct, HttpStatusCode.NotModified);
            _addresses[owner] = await WaitReadyAsync(name, $"run {owner:N}", ct);

            await PrepareAsync(owner, request, ct);
        }
        catch
        {
            await DeleteQuietlyAsync(owner, made);
            throw;
        }

        return new RunEnvironment(owner, workingDirectory);
    }

    public override async Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
    {
        await SyncPreviewDataBackAsync(environment, ct);
        _addresses.TryRemove(environment.OwnerId, out _);
        await DeleteObjectsAsync(environment.OwnerId, ct);
    }

    /// <summary>Which of a run's objects one <see cref="CreateAsync"/> call made, as opposed to found.</summary>
    private sealed class Made
    {
        public bool Container { get; set; }
        public bool Volume { get; set; }
        public bool Network { get; set; }
    }

    // Container first, then the volume it held, then the network once ILD has left it.
    private async Task DeleteObjectsAsync(Guid owner, CancellationToken ct, Made? only = null)
    {
        var name = Escaped(ObjectName(owner));
        if (only?.Container ?? true)
            await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{name}?force=true&v=true", null, ct));
        if (only?.Volume ?? true)
            await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/volumes/{name}?force=true", null, ct));
        if (!(only?.Network ?? true)) return;
        await RemoveNetworkAsync(name, ct);
    }

    /// <summary>Takes ILD off the network, then deletes it.</summary>
    private async Task RemoveNetworkAsync(string escapedName, CancellationToken ct)
    {
        if (_options.IldContainer is { } ild)
        {
            // Refused when ILD was never connected (the run failed before that); a connection that stays is what the network delete reports.
            try { await _api.PostAsync($"/networks/{escapedName}/disconnect", new { Container = ild, Force = true }, ct); }
            catch (DockerApiException) { }
        }
        await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/networks/{escapedName}", null, ct));
    }

    private async Task DeleteQuietlyAsync(Guid owner, Made made)
    {
        try
        {
            _addresses.TryRemove(owner, out _);
            await DeleteObjectsAsync(owner, CancellationToken.None, made);
        }
        catch (Exception ex) { Logger?.LogWarning(ex, "Could not remove the objects of run {Run} after its container failed to start; the next sweep removes them", owner); }
    }

    private const int SubnetAttempts = 8;

    /// <summary>
    /// Makes an internal network on the first free /28 of the subnet pool. A network with a subnet of its own is what lets ILD be put back on it
    /// at a fixed address, and does not use up the engine's default pools, which hold about 30 networks.
    /// </summary>
    private async Task<JsonNode?> CreateInternalNetworkAsync(string name, IDictionary<string, string> labels, CancellationToken ct)
    {
        NetworkSubnets.TryParse(_options.SubnetPool, out var pool);
        var refused = new HashSet<uint>();
        for (var attempt = 0; attempt < SubnetAttempts; attempt++)
        {
            var used = new List<(uint Start, int Prefix)>();
            foreach (var listed in (await _api.GetAsync("/networks", ct))?.AsArray() ?? [])
                foreach (var config in listed?["IPAM"]?["Config"]?.AsArray() ?? [])
                    if (config?["Subnet"]?.GetValue<string>() is { } cidr && NetworkSubnets.TryParse(cidr, out var subnet))
                        used.Add(subnet);

            var free = NetworkSubnets.Candidates(pool)
                .Cast<uint?>()
                .FirstOrDefault(c => !refused.Contains(c!.Value) && !used.Any(u => NetworkSubnets.Overlaps(u, (c.Value, NetworkSubnets.NetworkPrefix))));
            if (free is not { } start)
                throw new InvalidOperationException($"No free /{NetworkSubnets.NetworkPrefix} is left in {_options.SubnetPool} for the network {name}; widen {ContainerRunHostOptions.SubnetPoolSetting} or remove networks that are no longer used.");

            try
            {
                return await _api.PostAsync("/networks/create", new
                {
                    Name = name,
                    Internal = true,
                    CheckDuplicate = true,
                    Labels = labels,
                    IPAM = new { Config = new[] { new { Subnet = NetworkSubnets.Cidr(start, NetworkSubnets.NetworkPrefix), Gateway = NetworkSubnets.Gateway(start) } } },
                }, ct);
            }
            catch (DockerApiException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                // Another network took it since the list was read, or the engine knows of an overlap the list does not show (a route of the host).
                refused.Add(start);
                Logger?.LogInformation("The engine refused {Subnet} for the network {Name}, so the next one is tried: {Reason}", NetworkSubnets.Cidr(start, NetworkSubnets.NetworkPrefix), name, ex.Message);
            }
        }

        throw new InvalidOperationException($"The engine refused {SubnetAttempts} subnets of {_options.SubnetPool} in a row for the network {name}; check {ContainerRunHostOptions.SubnetPoolSetting} against the networks of the host.");
    }

    /// <summary>Makes the object; false when it was there already.</summary>
    private static async Task<bool> TolerateConflictAsync(Func<Task<JsonNode?>> create)
    {
        try { await create(); return true; }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.Conflict) { return false; }
    }

    private static async Task IgnoreNotFoundAsync(Func<Task<JsonNode?>> call)
    {
        try { await call(); }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound) { }
    }

    protected override Task<bool> IsRunningAsync(Guid owner, CancellationToken ct) => IsHealthyAsync(owner, ct);

    private async Task<bool> IsHealthyAsync(Guid owner, CancellationToken ct)
    {
        var container = await _api.GetAsync($"/containers/{Escaped(ObjectName(owner))}/json", ct, HttpStatusCode.NotFound);
        if (container?["State"]?["Running"]?.GetValue<bool>() != true) return false;
        var address = AddressIn(container, ObjectName(owner));
        if (address is null) return false;
        _addresses[owner] = address;
        return true;
    }

    private static string? AddressIn(JsonNode? container, string network)
        => container?["NetworkSettings"]?["Networks"]?[network]?["IPAddress"]?.GetValue<string>() is { Length: > 0 } ip ? ip : null;

    /// <summary>
    /// Where the run (or the chat container) reaches ILD on its network: ILD's own container, which is connected to the network, when ILD runs
    /// in one, and otherwise the network's gateway, which is the host.
    /// </summary>
    private async Task<string> IldAddressAsync(string network, string subject, CancellationToken ct)
    {
        if (_options.IldContainer is { } ild)
        {
            var inspected = await _api.GetAsync($"/containers/{Escaped(ild)}/json", ct)
                ?? throw new InvalidOperationException($"container {ild}, which {ContainerRunHostOptions.IldContainerSetting} names as ILD's own, cannot be read");
            if (AddressIn(inspected, network) is null)
            {
                await _api.PostAsync($"/networks/{Escaped(network)}/connect", new { Container = ild }, ct);
                inspected = await _api.GetAsync($"/containers/{Escaped(ild)}/json", ct);
            }
            return AddressIn(inspected, network)
                ?? throw new InvalidOperationException($"ILD's container {ild} has no address on the network of {subject}");
        }

        var created = await _api.GetAsync($"/networks/{Escaped(network)}", ct);
        return created?["IPAM"]?["Config"]?.AsArray().Select(c => c?["Gateway"]?.GetValue<string>()).FirstOrDefault(g => !string.IsNullOrEmpty(g))
            ?? throw new InvalidOperationException($"the network of {subject} has no gateway address, so it cannot reach ILD");
    }

    /// <summary>Where a container sits on a network of its own: the network, ILD's address on it, whether it has DNS, and the options of its state directory (none for a container that has no state directory).</summary>
    private sealed record Placement(string Network, string IldAddress, bool Dns, string? StateOptions = null);

    /// <summary>
    /// The create request every container ILD makes shares: the runner image as the runner user, with every capability
    /// dropped and no new privileges. A <paramref name="placement"/> puts it on a network of its own, reaching only ILD;
    /// without one it is on the engine's default network, as the sign-in needs to reach its provider. A run's
    /// <paramref name="limits"/> are its memory and CPU.
    /// </summary>
    private object ContainerBody(
        IEnumerable<string> env, IDictionary<string, string> labels, IEnumerable<string> binds,
        string? workingDirectory = null, Placement? placement = null, (long Memory, long NanoCpus)? limits = null)
    {
        var host = new Dictionary<string, object>
        {
            ["Binds"] = binds.ToArray(),
            ["CapDrop"] = new[] { "ALL" },
            ["SecurityOpt"] = new[] { "no-new-privileges" },
            ["RestartPolicy"] = new { Name = "no" },
        };
        var body = new Dictionary<string, object>
        {
            ["Image"] = _options.RunnerImage,
            ["Entrypoint"] = new[] { "/usr/bin/tini", "--" },
            ["Cmd"] = new[] { "sleep", "infinity" },
            ["User"] = $"{RunnerUid}:{RunnerUid}",
            ["Env"] = env.ToArray(),
            ["Labels"] = labels,
            ["HostConfig"] = host,
        };
        if (workingDirectory is not null) body["WorkingDir"] = workingDirectory;
        if (limits is { } l)
        {
            host["Memory"] = l.Memory;
            host["NanoCpus"] = l.NanoCpus;
        }
        if (placement is not null)
        {
            if (placement.StateOptions is not null)
                host["Tmpfs"] = new Dictionary<string, string> { [RunPaths.StateDirectory] = $"rw,nosuid,nodev,size=64m,mode=0700,{placement.StateOptions}" };
            host["NetworkMode"] = placement.Network;
            host["ExtraHosts"] = IldHosts(placement.IldAddress);
            host["Dns"] = NameServers(placement.Dns);
            body["NetworkingConfig"] = new { EndpointsConfig = new Dictionary<string, object> { [placement.Network] = new { } } };
        }
        return body;
    }

    /// <summary>
    /// Creates the container; false when it is there already and <paramref name="tolerateConflict"/> says that is all right.
    /// A missing runner image is reported as what to do about it.
    /// </summary>
    private async Task<bool> PostCreateAsync(string name, object body, bool tolerateConflict, CancellationToken ct)
    {
        try
        {
            await _api.PostAsync($"/containers/create?name={Escaped(name)}", body, ct);
            return true;
        }
        catch (DockerApiException ex) when (tolerateConflict && ex.Status == HttpStatusCode.Conflict)
        {
            return false;
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"the runner image {_options.RunnerImage} is not on this machine; build or pull it first ({ex.Message})", ex);
        }
    }

    /// <summary>Makes the run's container; false when an attempt had left it.</summary>
    private async Task<bool> CreateContainerAsync(Guid owner, string ildAddress, RunToolEnvironment? tools, bool dns, CancellationToken ct)
    {
        var name = ObjectName(owner);
        var body = ContainerBody(
            env: new[] { $"HOME={RunPaths.HomeDirectory(owner)}" }
                .Concat(_options.GitIdentity.Select(e => $"{e.Key}={e.Value}"))
                .Concat(tools?.For(RunPaths.HomeDirectory(owner)) ?? []),
            labels: Labels(owner),
            binds: new[] { $"{name}:{RunPaths.RunsRoot}:rw" }.Concat(ToolBinds(tools?.Zones ?? [])),
            // The volume's root, which the image gives the runner. An engine makes a missing working directory as root
            // (Podman does), and the runner could then not make the run's own directory in it: the runner makes that.
            workingDirectory: RunPaths.RunsRoot,
            placement: new Placement(name, ildAddress, dns, await StateOwnerOptionAsync(ct)),
            limits: (_options.MemoryBytes ?? 0, _options.NanoCpus ?? 0));
        // False when an attempt had left the container, or it is the run's own on a retry: started by the caller.
        return await PostCreateAsync(name, body, tolerateConflict: true, ct);
    }

    // -- Toolchains --------------------------------------------------------------

    private IDictionary<string, string> ToolsLabels() => new Dictionary<string, string>
    {
        [RunPaths.ManagedByLabel] = RunPaths.ManagedByValue,
        [RunPaths.RoleLabel] = RunPaths.ToolsRole,
    };

    /// <summary>
    /// What the run's commands get from the toolchain store: the toolchains its repository needs and the agents ILD has
    /// installed, each zone installed first when it lacks them. Null when toolchains are off, so the run has only what
    /// the runner image has.
    /// </summary>
    protected override Task<RunToolEnvironment?> ToolsForAsync(string owner, RunToolPlan plan, Guid? repositoryId, CancellationToken ct)
        => ToolsForAsync(owner, plan, Agents(), repositoryId, ct);

    protected override bool ChatToolchains => _options.Toolchains;

    private async Task<RunToolEnvironment?> ToolsForAsync(string owner, RunToolPlan plan, IReadOnlyList<RunAgent> agents, Guid? repositoryId, CancellationToken ct)
        => _options.Toolchains ? await ToolZonesAsync(owner, plan, agents, repositoryId, ct) : null;

    // The zones every chat mounts: the agents' and the shared toolchains.
    private static readonly RunToolMount[] ChatToolZones = [new(RunToolZone.Agents), new(RunToolZone.Toolchains)];

    private IEnumerable<string> ToolBinds(IEnumerable<RunToolMount> zones)
        => zones.Select(z => $"{z.VolumeName(_options.ToolsVolume)}:{z.MountPath}:ro");

    protected override Task PrepareToolZoneAsync(RunToolMount zone, CancellationToken ct)
        => TolerateConflictAsync(() => _api.PostAsync("/volumes/create", new { Name = zone.VolumeName(_options.ToolsVolume), Labels = ToolsLabels() }, ct));

    protected override string ToolZoneName(RunToolMount zone) => zone.VolumeName(_options.ToolsVolume);

    /// <summary>
    /// A short-lived container of the runner image with only the zone being installed mounted writable (and the shared
    /// toolchains read-only for a repository's) and a network of its own that is internal and reaches only ILD, so mise and npm download through
    /// the egress proxy and nothing else; removed, with the network, when <paramref name="work"/> ends. It carries no run label, so the sweep never takes it for a run's.
    /// </summary>
    protected override async Task<T> WithToolInstallerAsync<T>(ToolInstaller toolInstaller, Func<Guid, Task<T>> work, CancellationToken ct)
    {
        var installer = Guid.NewGuid();
        var name = $"ild-tools-{installer:N}";
        _execNames[installer] = name;
        var labels = ToolsLabels();
        labels[RunPaths.InstanceLabel] = _options.Instance;
        try
        {
            string[] binds =
            [
                $"{toolInstaller.Writable.VolumeName(_options.ToolsVolume)}:{toolInstaller.Writable.MountPath}:rw",
                .. toolInstaller.ReadOnly is { } shared ? ToolBinds([shared]) : [],
            ];
            await CreateInternalNetworkAsync(name, labels, ct);
            var ild = await IldAddressAsync(name, "the toolchain installer", ct);
            await PostCreateAsync(
                name, ContainerBody(toolInstaller.Environment, labels, binds, placement: new Placement(name, ild, Dns: false)), tolerateConflict: false, ct);
            await _api.PostAsync($"/containers/{Escaped(name)}/start", null, ct, HttpStatusCode.NotModified);
            await WaitRunningAsync(name, ct);
            return await work(installer);
        }
        finally
        {
            _execNames.TryRemove(installer, out _);
            await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{Escaped(name)}?force=true", null, CancellationToken.None));
            await RemoveNetworkAsync(Escaped(name), CancellationToken.None);
        }
    }

    private async Task WaitRunningAsync(string name, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var container = await _api.GetAsync($"/containers/{Escaped(name)}/json", ct)
                ?? throw new InvalidOperationException($"the toolchain installer {name} is gone");
            if (container["State"]?["Running"]?.GetValue<bool>() == true) return;
            if (container["State"]?["Status"]?.GetValue<string>() is "exited" or "dead")
                throw new InvalidOperationException($"the toolchain installer {name} ended before it was ready: {WhyNotReady(container)}");
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the toolchain installer {name} was not ready after {_options.ReadyTimeout.TotalSeconds:0} s: {WhyNotReady(container)}");
            await Task.Delay(ReadyPollInterval, ct);
        }
    }

    /// <summary>
    /// The tmpfs option that hands the state directory to the runner's uid. Docker passes uid/gid on to the kernel;
    /// Podman refuses them as unknown and has its own <c>U</c>, which chowns the mount to the container's user.
    /// </summary>
    private async Task<string> StateOwnerOptionAsync(CancellationToken ct)
    {
        var podman = _podman ?? await DetectPodmanAsync(ct);
        return podman ? "U" : $"uid={RunnerUid},gid={RunnerUid}";
    }

    // Only a definite answer is kept: a 5xx or a socket proxy's 403 says nothing about the engine, and a wrong "Docker"
    // would hand every container options Podman refuses until ILD restarts.
    private async Task<bool> DetectPodmanAsync(CancellationToken ct)
    {
        var (status, _) = await _api.InfoAsync(libpod: true, ct);
        if ((int)status is >= 200 and < 300) { _podman = true; return true; }
        if (status == HttpStatusCode.NotFound) { _podman = false; return false; }

        // The Docker-compatible API names its engine: Podman lists a "Podman Engine" component.
        try
        {
            if (await _api.GetAsync("/version", ct) is { } version && version["Components"] is JsonArray components)
            {
                var podman = components.Any(c => c?["Name"]?.GetValue<string>()?.Contains("Podman", StringComparison.OrdinalIgnoreCase) == true);
                _podman = podman;
                return podman;
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or SocketException or JsonException or InvalidOperationException)
        {
            Logger?.LogWarning(ex, "Could not tell whether the engine is Podman; this container gets the Docker state options");
        }
        return false;
    }

    private async Task<string> WaitReadyAsync(string name, string subject, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var container = await _api.GetAsync($"/containers/{Escaped(name)}/json", ct)
                ?? throw new InvalidOperationException($"the container of {subject} is gone");
            if (container["State"]?["Running"]?.GetValue<bool>() == true && AddressIn(container, name) is { } address)
                return address;
            if (container["State"]?["Status"]?.GetValue<string>() is "exited" or "dead")
                throw new InvalidOperationException($"the container of {subject} ended before it was ready: {WhyNotReady(container)}");
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the container of {subject} was not ready after {_options.ReadyTimeout.TotalSeconds:0} s: {WhyNotReady(container)}");
            await Task.Delay(ReadyPollInterval, ct);
        }
    }

    private static string WhyNotReady(JsonNode container)
    {
        var state = container["State"];
        return state?["Error"]?.GetValue<string>() is { Length: > 0 } error ? error
            : state?["Status"]?.GetValue<string>() is { } status ? $"{status}, exit code {state["ExitCode"]}"
            : "no status";
    }

    // A chat container that is stopped has nothing running to kill, and asking would start it again.
    protected override async Task<bool> RunExistsAsync(ExecOwner owner, CancellationToken ct)
        => owner.Chat ? await ChatContainerRunningAsync(ct) : await _api.GetAsync($"/containers/{Escaped(ObjectName(owner.Id))}/json", ct, HttpStatusCode.NotFound) is not null;

    // The engine's exec-create takes the environment in its own field, so it never travels in the command.
    protected override bool ExecTakesEnvironment => true;

    protected override Task<IExecStream> OpenExecStreamAsync(
        ExecOwner owner, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, bool stdin, bool tty, CancellationToken ct)
        => OpenExecStreamAsync(owner, command, environment, stdin, tty, user: null, ct);

    private async Task<IExecStream> OpenExecStreamAsync(
        ExecOwner owner, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment, bool stdin, bool tty, string? user, CancellationToken ct)
    {
        if (owner.Chat)
            return await DockerExec.OpenAsync(_api, await EnsureChatContainerAsync(ct), command, stdin, tty, ExitStatusTimeout, ct, user, environment);
        if (_execNames.TryGetValue(owner.Id, out var other))
            return await DockerExec.OpenAsync(_api, other, command, stdin, tty, ExitStatusTimeout, ct, user, environment);
        try
        {
            return await DockerExec.OpenAsync(_api, ObjectName(owner.Id), command, stdin, tty, ExitStatusTimeout, ct, user, environment);
        }
        catch (DockerApiException)
        {
            // Stopped when ILD shut down, or by a restart of the engine or machine: started again, with all it held. Engines
            // refuse an exec into a stopped container with different statuses (Docker 409, Podman 500), so its state decides.
            if (!await RunContainerStoppedAsync(owner.Id, ct)) throw;
            await StartStoppedRunAsync(owner.Id, ct);
            return await DockerExec.OpenAsync(_api, ObjectName(owner.Id), command, stdin, tty, ExitStatusTimeout, ct, user, environment);
        }
    }

    // Docker and Podman make the hosts file root's in each container, and root writes it without a capability, as it does
    // resolv.conf: the container keeps its capabilities and its commands keep their user.
    protected override async Task<RunHostOutput> RunHostsScriptAsync(ExecOwner owner, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var exec = await OpenExecStreamAsync(owner, ["/bin/bash", "-c", HostsScript, "bash", .. arguments], environment: null, stdin: false, tty: false, user: "0", ct);
        var output = new StreamReader(exec.Stdout).ReadToEndAsync(ct);
        var error = new StreamReader(exec.Stderr!).ReadToEndAsync(ct);
        var status = await exec.Status;
        await Task.WhenAll(output, error);
        return new RunHostOutput(status ?? -1, await output, await error);
    }

    private async Task<bool> RunContainerStoppedAsync(Guid owner, CancellationToken ct)
    {
        var container = await _api.GetAsync($"/containers/{Escaped(ObjectName(owner))}/json", ct, HttpStatusCode.NotFound);
        return container?["State"]?["Status"]?.GetValue<string>() is "exited" or "stopped" or "created";
    }

    private async Task StartStoppedRunAsync(Guid owner, CancellationToken ct)
    {
        var name = ObjectName(owner);
        Logger?.LogInformation("Starting the stopped container of run {Run} again", owner);
        await _api.PostAsync($"/containers/{Escaped(name)}/start", null, ct, HttpStatusCode.NotModified);
        // Its address on its network can change with a start.
        _addresses[owner] = await WaitReadyAsync(name, $"run {owner:N}", ct);
    }

    // -- Direct route (network mode Off) ------------------------------------------

    /// <summary>The network that gives a container in network mode Off its route out: not internal, with DNS, and one for every run and the chats.</summary>
    internal string OpenNetworkName => $"ild-open-{_options.Instance}";

    private IDictionary<string, string> OpenLabels() => new Dictionary<string, string>
    {
        [RunPaths.ManagedByLabel] = RunPaths.ManagedByValue,
        [RunPaths.InstanceLabel] = _options.Instance,
        [RunPaths.RoleLabel] = RunPaths.OpenRole,
    };

    private string ContainerOf(ExecOwner owner) => owner.Chat ? ChatName : ObjectName(owner.Id);

    private async Task<bool> OnOpenNetworkAsync(string container, CancellationToken ct)
        => (await _api.GetAsync($"/containers/{Escaped(container)}/json", ct, HttpStatusCode.NotFound))?["NetworkSettings"]?["Networks"]?[OpenNetworkName] is not null;

    // A run made with DNS on has the engine's own nameserver (no Dns in its HostConfig), which closing the route writes back.
    private async Task<bool> UsesEngineDnsAsync(string container, CancellationToken ct)
        => (await _api.GetAsync($"/containers/{Escaped(container)}/json", ct))?["HostConfig"]?["Dns"] is not JsonArray { Count: > 0 };

    protected override async Task OpenDirectRouteAsync(ExecOwner owner, CancellationToken ct)
    {
        if (owner.Chat) await EnsureChatContainerAsync(ct);
        var container = ContainerOf(owner);
        await TolerateConflictAsync(() => _api.PostAsync("/networks/create", new { Name = OpenNetworkName, Internal = false, CheckDuplicate = true, Labels = OpenLabels() }, ct));
        if (!await OnOpenNetworkAsync(container, ct))
        {
            try { await _api.PostAsync($"/networks/{Escaped(OpenNetworkName)}/connect", new { Container = container }, ct); }
            // Another launch, or an earlier attempt, connected it first.
            catch (DockerApiException)
            {
                if (!await OnOpenNetworkAsync(container, ct)) throw;
            }
        }
        // The container was made with no nameserver, which HostConfig fixes; root, who owns the file, writes one for as long as it has the route.
        await SetNameServerAsync(container, await GatewayNameServerAsync(OpenNetworkName, ct), ct);
    }

    protected override async Task CloseDirectRouteAsync(ExecOwner owner, CancellationToken ct)
    {
        var container = ContainerOf(owner);
        if (!await OnOpenNetworkAsync(container, ct)) return;
        // What the container was made with: nothing when its HostConfig has a nameserver, else the engine's own for its network.
        await SetNameServerAsync(container, await UsesEngineDnsAsync(container, ct) ? await GatewayNameServerAsync(container, ct) : NameServers(dns: false)[0], ct);
        await _api.PostAsync($"/networks/{Escaped(OpenNetworkName)}/disconnect", new { Container = container }, ct);
    }

    protected override async Task<bool> HasDirectRouteAsync(ExecOwner owner, CancellationToken ct)
        => await OnOpenNetworkAsync(ContainerOf(owner), ct);

    public override async Task<IReadOnlyList<DirectRoute>> DirectRoutesAsync(CancellationToken ct = default)
    {
        var routes = new List<DirectRoute>();
        var network = await _api.GetAsync($"/networks/{Escaped(OpenNetworkName)}", ct, HttpStatusCode.NotFound);
        foreach (var attached in (network?["Containers"] as JsonObject)?.Select(c => c.Value?["Name"]?.GetValue<string>()?.TrimStart('/')) ?? [])
        {
            if (attached == ChatName) routes.Add(new DirectRoute(Guid.Empty, Chat: true));
            else if (attached?.StartsWith("ild-run-", StringComparison.Ordinal) == true && Guid.TryParseExact(attached["ild-run-".Length..], "N", out var run))
                routes.Add(new DirectRoute(run, Chat: false));
        }
        return routes;
    }

    /// <summary>
    /// Where a container on the open network resolves names: Podman's DNS answers on the network's gateway; Docker's resolver is in
    /// every container on a network of its own, at 127.0.0.11.
    /// </summary>
    private async Task<string> GatewayNameServerAsync(string network, CancellationToken ct)
    {
        if (!(_podman ?? await DetectPodmanAsync(ct))) return "127.0.0.11";
        var inspected = await _api.GetAsync($"/networks/{Escaped(network)}", ct);
        return inspected?["IPAM"]?["Config"]?.AsArray().Select(c => c?["Gateway"]?.GetValue<string>()).FirstOrDefault(g => !string.IsNullOrEmpty(g))
            ?? throw new InvalidOperationException($"the network {network} has no gateway address, which is where names are resolved on it");
    }

    // Root owns the file and needs no capability to write it; a failed write is reported, since the redirect's status is the script's.
    private async Task SetNameServerAsync(string container, string nameServer, CancellationToken ct)
    {
        const string script = "printf 'nameserver %s\\n' \"$1\" > /etc/resolv.conf";
        using var exec = await DockerExec.OpenAsync(
            _api, container, ["/bin/sh", "-c", script, "sh", nameServer], stdin: false, tty: false, ExitStatusTimeout, ct, user: "0");
        var output = new StreamReader(exec.Stdout).ReadToEndAsync(ct);
        var error = new StreamReader(exec.Stderr!).ReadToEndAsync(ct);
        var status = await exec.Status;
        await Task.WhenAll(output, error);
        if (status != 0)
            throw new InvalidOperationException($"could not set the nameserver of {container}: {(await error).Trim()}");
    }

    // -- Shutdown ----------------------------------------------------------------

    /// <summary>
    /// What ILD puts away when it stops, after the drain has parked its runs. The chat container and any sign-in container
    /// are removed: what chats keep is in their volume, and the next chat makes the container from the runner image as
    /// it is then. Each run's container is stopped, not removed, so nothing runs while ILD is down while a parked run,
    /// its checkout and its agent session wait in it; ILD starts it again when the run next needs it.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        var work = new List<Task> { RemoveContainerAsync(ChatName, "the chat container", ct) };
        work.AddRange(_signIns.Keys.Select(name => RemoveContainerAsync(name, "a sign-in container", ct)));
        work.AddRange(_execNames.Values.Where(name => name.StartsWith("ild-tools-", StringComparison.Ordinal)).Select(name => RemoveContainerAsync(name, "a toolchain installer", ct)));
        try
        {
            var filter = DockerApiClient.LabelFilter($"{RunPaths.ManagedByLabel}={RunPaths.ManagedByValue}", $"{RunPaths.InstanceLabel}={_options.Instance}");
            foreach (var listed in (await _api.GetAsync($"/containers/json?filters={filter}", ct))?.AsArray() ?? [])
                if (listed?["Labels"]?[RunPaths.RunLabel]?.GetValue<string>() is { } run && Guid.TryParseExact(run, "N", out var owner))
                    work.Add(StopRunAsync(owner, ct));
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or SocketException or OperationCanceledException or JsonException)
        {
            Logger?.LogWarning(ex, "Could not list the containers of the runs to stop them; they keep running until ILD next needs them");
        }
        await Task.WhenAll(work);
    }

    private async Task StopRunAsync(Guid owner, CancellationToken ct)
    {
        try
        {
            await _api.PostAsync($"/containers/{Escaped(ObjectName(owner))}/stop?t=2", null, ct, HttpStatusCode.NotModified, HttpStatusCode.NotFound);
            _addresses.TryRemove(owner, out _);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or SocketException or OperationCanceledException)
        {
            Logger?.LogWarning(ex, "Could not stop the container of run {Run}", owner);
        }
    }

    private async Task RemoveContainerAsync(string name, string subject, CancellationToken ct)
    {
        try
        {
            await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{Escaped(name)}?force=true", null, ct));
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or SocketException or OperationCanceledException)
        {
            Logger?.LogWarning(ex, "Could not remove {Subject} {Name}; it is replaced when ILD next needs it", subject, name);
        }
    }

    // -- Chats -------------------------------------------------------------------

    protected override TimeSpan ChatIdleTimeout => _options.ChatIdleTimeout;

    // By its name, never by what ILD manages: a run's container, parked or waiting for a human, is not the chat's to reap.
    public override Task ReapIdleChatAsync(CancellationToken ct = default)
        => ReapChatAsync(_chatGate, ChatName, token => RemoveContainerAsync(ChatName, "the chat container", token), ct);

    /// <summary>The chat container, its volume and its network: one of each for the instance, kept running.</summary>
    internal string ChatName => $"ild-chat-{_options.Instance}";

    public override bool HostsChats => true;

    // No run label: the sweep, which deletes what a run left, never takes the chat container for one.
    private IDictionary<string, string> ChatLabels() => new Dictionary<string, string>
    {
        [RunPaths.ManagedByLabel] = RunPaths.ManagedByValue,
        [RunPaths.InstanceLabel] = _options.Instance,
        [RunPaths.RoleLabel] = RunPaths.ChatRole,
    };

    /// <summary>The name of the chat container, made and started first when it is missing or stopped. The volume keeps the chats' folders through that.</summary>
    private async Task<string> EnsureChatContainerAsync(CancellationToken ct)
    {
        if (!ChatBeingRemoved && await ChatContainerRunningAsync(ct)) return ChatName;

        await _chatGate.WaitAsync(ct);
        try
        {
            if (await ChatContainerRunningAsync(ct)) return ChatName;

            await TolerateConflictAsync(() => _api.PostAsync("/volumes/create", new { Name = ChatName, Labels = ChatLabels() }, ct));
            if (_options.Toolchains)
                foreach (var zone in ChatToolZones)
                    await PrepareToolZoneAsync(zone, ct);
            await TolerateConflictAsync(() => CreateInternalNetworkAsync(ChatName, ChatLabels(), ct));
            // A stopped one is replaced; what it held is in the volume.
            await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{Escaped(ChatName)}?force=true", null, ct));
            var ild = await IldAddressAsync(ChatName, "the chat container", ct);
            await CreateChatContainerAsync(ild, ct);
            await _api.PostAsync($"/containers/{Escaped(ChatName)}/start", null, ct, HttpStatusCode.NotModified);
            await WaitReadyAsync(ChatName, "the chats", ct);
            if (TakeRemovedForIdle()) Logger?.LogInformation("The chat container {Name} is back, made for a chat after it was removed for idleness", ChatName);
            return ChatName;
        }
        finally
        {
            _chatGate.Release();
        }
    }

    private async Task<bool> ChatContainerRunningAsync(CancellationToken ct)
    {
        var container = await _api.GetAsync($"/containers/{Escaped(ChatName)}/json", ct, HttpStatusCode.NotFound);
        return container?["State"]?["Running"]?.GetValue<bool>() == true && AddressIn(container, ChatName) is not null
            && container["Config"]?["Labels"]?[SpecLabel]?.GetValue<string>() == await ChatSpecAsync(ct);
    }

    /// <summary>The label that says which runner image, and which of ILD's settings, the chat container was made with.</summary>
    internal const string SpecLabel = "ild/spec";

    private string? _chatSpec;

    /// <summary>
    /// What the chat container is made from: the runner image's id, so a rebuilt or pulled image under the same name
    /// counts, and the settings it is given. A chat container made from anything else is replaced, since the chat
    /// container is long-lived and nothing else would ever bring it up to date. Read once per ILD process.
    /// </summary>
    private async Task<string> ChatSpecAsync(CancellationToken ct)
    {
        if (_chatSpec is { } known) return known;
        // The image's name is the path: Docker and Podman both take one with slashes there.
        var image = await _api.GetAsync($"/images/{_options.RunnerImage}/json", ct, HttpStatusCode.NotFound);
        string[] parts =
        [
            image?["Id"]?.GetValue<string>() ?? "", _options.RunnerImage, _options.IldHost, _options.ToolsVolume,
            _options.Toolchains ? "tool-zones" : "no-tools", "no-dns",
            .. _options.GitIdentity.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}"),
        ];
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts)));
        return _chatSpec = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Removes a chat container made from another runner image or other settings, so it is not left running until the next chat.</summary>
    internal async Task RetireStaleChatContainerAsync(CancellationToken ct)
    {
        var container = await _api.GetAsync($"/containers/{Escaped(ChatName)}/json", ct, HttpStatusCode.NotFound);
        if (container is null || container["Config"]?["Labels"]?[SpecLabel]?.GetValue<string>() == await ChatSpecAsync(ct)) return;
        Logger?.LogInformation("Replacing the chat container: the runner image or ILD's settings changed since it was made");
        await RemoveContainerAsync(ChatName, "the chat container", ct);
    }

    private string[] IldHosts(string ildAddress)
        => RunFacingHost(_options.IldHost) == _options.IldHost
            ? [$"{_options.IldHost}:{ildAddress}"]
            : [$"{_options.IldHost}:{ildAddress}", $"{RunFacingHost(_options.IldHost)}:{ildAddress}"];

    // Names are resolved only by ILD's egress proxy: ILD's own name comes from ExtraHosts, and the nameserver is nothing, so an
    // engine that forwards DNS out of an internal network has nowhere to forward to.
    private static string[] NameServers(bool dns) => dns ? [] : ["127.0.0.1"];

    private async Task CreateChatContainerAsync(string ildAddress, CancellationToken ct)
    {
        // The same settings as a run's container, but no memory or CPU limit, as chats had in ILD's own container.
        var body = ContainerBody(
            env: _options.GitIdentity.Select(e => $"{e.Key}={e.Value}"),
            labels: new Dictionary<string, string>(ChatLabels()) { [SpecLabel] = await ChatSpecAsync(ct) },
            // Every chat shares this container, so it mounts the agents and the shared toolchains, never a repository's own tools.
            binds: new[] { $"{ChatName}:{RunPaths.ChatsRoot}:rw" }.Concat(ToolBinds(_options.Toolchains ? ChatToolZones : [])),
            workingDirectory: RunPaths.ChatsRoot,
            placement: new Placement(ChatName, ildAddress, Dns: false, await StateOwnerOptionAsync(ct)));
        // Made by another request a moment ago, when it is there already: started by the caller.
        await PostCreateAsync(ChatName, body, tolerateConflict: true, ct);
    }

    // -- Provider sign-in --------------------------------------------------------

    private const string SignInRole = "signin";

    private static string SignInName(Guid id) => $"ild-signin-{id:N}";

    // No run label: the sweep, which deletes what a run left, never takes it for a run's.
    private IDictionary<string, string> SignInLabels() => new Dictionary<string, string>
    {
        [RunPaths.ManagedByLabel] = RunPaths.ManagedByValue,
        [RunPaths.InstanceLabel] = _options.Instance,
        [RunPaths.RoleLabel] = SignInRole,
    };

    /// <summary>
    /// A terminal for a provider's login in a short-lived container of the runner image of its own: the agent from the toolchain
    /// store, mounted read-only (installed first if it is missing), the engine's default network for the OAuth flow, and
    /// none of what a chat has. It writes the credentials that the model gateway keeps from agents, so no chat or run
    /// shares it. The container goes when the result is disposed, or on a failure to start.
    /// </summary>
    public async Task<SignInTerminal> OpenSignInAsync(ManagedAgent? agent, string command, IReadOnlyList<string> arguments, int cols, int rows, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var name = SignInName(id);
        _signIns[name] = 0;
        _execNames[id] = name;

        async Task RemoveAsync()
        {
            _execNames.TryRemove(id, out _);
            // Forgotten whether or not the delete works, so the sweep can remove a container that it did not.
            _signIns.TryRemove(name, out _);
            try { await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{Escaped(name)}?force=true&v=true", null, CancellationToken.None)); }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                Logger?.LogWarning(ex, "Could not remove the sign-in container {Name}; the next sweep removes it", name);
            }
        }

        try
        {
            var agents = agent is null ? (IReadOnlyList<RunAgent>)[] : Agents().Where(a => a.Agent.Key == agent.Key).ToList();
            var tools = await ToolsForAsync("The provider sign-in", new RunToolPlan([], []), agents, null, ct);
            // The engine's default network, for the OAuth flow; no volume of a run or chat, no ILD address.
            await PostCreateAsync(
                name,
                ContainerBody(new[] { $"HOME={SignInHome}" }.Concat(tools?.For(SignInHome) ?? []), SignInLabels(), ToolBinds(tools?.Zones ?? [])),
                tolerateConflict: false,
                ct);
            await _api.PostAsync($"/containers/{Escaped(name)}/start", null, ct, HttpStatusCode.NotModified);
            await WaitRunningAsync(name, ct);

            var exec = await OpenExecAsync(id, SignInTerminalCommand(command, arguments), environment: null, stdin: true, tty: true, ct);
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
        if (!_addresses.TryGetValue(owner, out var address))
        {
            var container = await _api.GetAsync($"/containers/{Escaped(ObjectName(owner))}/json", ct);
            address = AddressIn(container, ObjectName(owner)) ?? throw new InvalidOperationException($"the container of run {owner:N} has no address yet");
            _addresses[owner] = address;
        }
        return new UriBuilder(Uri.UriSchemeHttp, address, port).Uri;
    }

    // -- Sweep -------------------------------------------------------------------

    public override async Task<IReadOnlyList<RunLeftover>> ListLeftoversAsync(IReadOnlySet<Guid> liveIds, CancellationToken ct = default)
    {
        try
        {
            var retained = await RetainedRuns(ct);
            var filter = DockerApiClient.LabelFilter($"{RunPaths.ManagedByLabel}={RunPaths.ManagedByValue}", $"{RunPaths.InstanceLabel}={_options.Instance}");
            var runs = new HashSet<string>(StringComparer.Ordinal);
            void Collect(JsonNode? items, Func<JsonNode, JsonNode?> labelsOf)
            {
                foreach (var item in items?.AsArray() ?? [])
                    if (item is not null && labelsOf(item)?[RunPaths.RunLabel]?.GetValue<string>() is { } run)
                        runs.Add(run);
            }

            var containers = await _api.GetAsync($"/containers/json?all=true&filters={filter}", ct);
            Collect(containers, c => c["Labels"]);
            Collect((await _api.GetAsync($"/volumes?filters={filter}", ct))?["Volumes"], v => v["Labels"]);
            var networks = await _api.GetAsync($"/networks?filters={filter}", ct);
            Collect(networks, n => n["Labels"]);
            var signIns = new List<string>();
            var open = _execNames.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var listed in containers?.AsArray() ?? [])
            {
                var role = listed?["Labels"]?[RunPaths.RoleLabel]?.GetValue<string>();
                if (role is SignInRole or RunPaths.ToolsRole
                    && listed!["Names"]?.AsArray().FirstOrDefault()?.GetValue<string>()?.TrimStart('/') is { } name
                    && !_signIns.ContainsKey(name) && !open.Contains(name))
                    signIns.Add(name);
            }
            // An installer's network is removed with it; one whose container is gone, or never came, is a leftover of its own.
            foreach (var listed in networks?.AsArray() ?? [])
                if (listed?["Labels"]?[RunPaths.RoleLabel]?.GetValue<string>() == RunPaths.ToolsRole
                    && listed["Name"]?.GetValue<string>() is { } network && !open.Contains(network) && !signIns.Contains(network))
                    signIns.Add(network);

            // The open network is shared; it is a leftover once nothing is on it, and made again by the next launch in mode Off.
            if (networks?.AsArray().Any(n => n?["Name"]?.GetValue<string>() == OpenNetworkName) == true
                && ((await _api.GetAsync($"/networks/{Escaped(OpenNetworkName)}", ct, HttpStatusCode.NotFound))?["Containers"] as JsonObject)?.Count is null or 0)
                signIns.Add(OpenNetworkName);

            return runs
                .Where(run => !Guid.TryParseExact(run, "N", out var id) || !liveIds.Contains(id) && !retained.Contains(id))
                .Select(run => new RunLeftover(RunLeftoverKind, run))
                .Concat(signIns.Select(name => new RunLeftover(RunLeftoverKind, name)))
                .ToList();
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
        {
            Logger?.LogWarning(ex, "Could not list the containers runs left behind at {Socket}; the next sweep tries again", _options.Socket);
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
                await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{Escaped(leftover.Location)}?force=true&v=true", null, ct));
                if (leftover.Location.StartsWith("ild-tools-", StringComparison.Ordinal))
                    await RemoveNetworkAsync(Escaped(leftover.Location), ct);
                return true;
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                Logger?.LogWarning(ex, "Could not delete the sign-in or installer container {Name}; the next sweep tries again", leftover.Location);
                return false;
            }
        }
        if (leftover.Location == OpenNetworkName)
        {
            try
            {
                await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/networks/{Escaped(OpenNetworkName)}", null, ct));
                return true;
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                Logger?.LogWarning(ex, "Could not delete the network {Name}; the next sweep tries again", OpenNetworkName);
                return false;
            }
        }
        if (!Guid.TryParseExact(leftover.Location, "N", out var owner))
            return false;

        try
        {
            await DeleteObjectsAsync(owner, ct);
            _addresses.TryRemove(owner, out _);
            return true;
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
        {
            Logger?.LogWarning(ex, "Could not delete the objects of run {Run}; the next sweep tries again", owner);
            return false;
        }
    }

    // -- Socket ------------------------------------------------------------------

    public override async Task CheckAsync(CancellationToken ct = default)
    {
        if (await WarningAsync(ct) is { } warning)
            Logger?.LogWarning("{Warning}", warning);
        await ReconnectToRunsAsync(ct);
        try
        {
            await RetireStaleChatContainerAsync(ct);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or SocketException)
        {
            Logger?.LogWarning(ex, "Could not check the chat container; it is checked again when a chat next needs it");
        }
    }

    /// <summary>
    /// Puts ILD on a network at the address the run has for it. A network from before networks got subnets has none ILD may choose an address in,
    /// and the engine refuses; ILD is then connected at an address of the engine's choosing, which the run does not know.
    /// </summary>
    private async Task ConnectIldAsync(string network, string subject, string ild, string? address, CancellationToken ct)
    {
        if (address is not null)
        {
            try
            {
                await _api.PostAsync($"/networks/{Escaped(network)}/connect", new { Container = ild, EndpointConfig = new { IPAMConfig = new { IPv4Address = address } } }, ct);
                Logger?.LogInformation("Reconnected ILD to the network of {Subject} at {Address}", subject, address);
                return;
            }
            catch (DockerApiException ex)
            {
                Logger?.LogWarning("The engine refused to put ILD back on the network of {Subject} at {Address}, so ILD is connected at another address and the container cannot reach ILD until its container is removed and made again: {Reason}",
                    subject, address, ex.Message);
            }
        }

        await _api.PostAsync($"/networks/{Escaped(network)}/connect", new { Container = ild }, ct);
        if (address is null) Logger?.LogInformation("Reconnected ILD to the network of {Subject} at a new address", subject);
    }

    /// <summary>
    /// Puts ILD's own container back on the network of every run of this instance, a stopped one included, and of the chat container,
    /// at the address each was given for ILD. A container that is recreated (an upgrade, <c>docker compose up</c>)
    /// starts on none of them, and without this the runs and chats it left could reach neither the model gateway nor
    /// the MCP endpoint, and their previews could not be reached. A restarted container keeps its networks, and
    /// nothing is done.
    /// </summary>
    internal async Task ReconnectToRunsAsync(CancellationToken ct)
    {
        if (_options.IldContainer is not { } ild) return;
        try
        {
            var self = await _api.GetAsync($"/containers/{Escaped(ild)}/json", ct, HttpStatusCode.NotFound);
            if (self is null) return;
            var filter = DockerApiClient.LabelFilter($"{RunPaths.ManagedByLabel}={RunPaths.ManagedByValue}", $"{RunPaths.InstanceLabel}={_options.Instance}");
            var networks = new List<(string Network, string Subject)>();
            foreach (var listed in (await _api.GetAsync($"/containers/json?all=true&filters={filter}", ct))?.AsArray() ?? [])
            {
                if (listed?["Labels"]?[RunPaths.RunLabel]?.GetValue<string>() is { } run && Guid.TryParseExact(run, "N", out var owner))
                    networks.Add((ObjectName(owner), $"run {owner:N}"));
                else if (listed?["Labels"]?[RunPaths.RoleLabel]?.GetValue<string>() == RunPaths.ChatRole)
                    networks.Add((ChatName, "the chat container"));
            }

            foreach (var (network, subject) in networks)
            {
                if (AddressIn(self, network) is not null) continue;

                var container = await _api.GetAsync($"/containers/{Escaped(network)}/json", ct, HttpStatusCode.NotFound);
                var prefix = $"{_options.IldHost}:";
                var address = container?["HostConfig"]?["ExtraHosts"]?.AsArray()
                    .Select(h => h?.GetValue<string>())
                    .FirstOrDefault(h => h?.StartsWith(prefix, StringComparison.Ordinal) == true)?[prefix.Length..];
                try
                {
                    await ConnectIldAsync(network, subject, ild, address, ct);
                }
                catch (DockerApiException ex)
                {
                    Logger?.LogWarning("Could not put ILD back on the network of {Subject} at {Address}; it cannot reach ILD until it is retried: {Reason}",
                        subject, address, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or SocketException)
        {
            Logger?.LogWarning(ex, "Could not put ILD back on the networks of its runs and chats at {Socket}", _options.Socket);
        }
    }

    /// <summary>
    /// Says so when ILD cannot reach the engine, since no run can start then, and when the socket is rootful: whoever
    /// holds it controls every container on the host, which is root there. Asks Podman's own info first, then Docker's;
    /// says nothing about rootless when the engine answers but neither tells.
    /// </summary>
    public override async Task<string?> WarningAsync(CancellationToken ct = default)
    {
        if (_warning is not null) return _warning;
        // A socket that stalls must not hold the settings page for the HTTP client's own five minutes.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var rootless = await IsRootlessAsync(limit.Token);
            if (rootless != false) return null;
            return _warning = $"The container engine at {_options.Socket} is not rootless, so whoever holds that socket is root on this machine. Use rootless Podman (or rootless Docker) for runs.";
        }
        // Not kept: the socket may come back, and then the warning goes.
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or IOException or SocketException or OperationCanceledException))
        {
            var reason = ex is OperationCanceledException ? "it did not answer within five seconds" : ReasonOf(ex);
            return $"ILD cannot reach the container engine at {_options.Socket} ({reason}), so no run can start. Check that the engine's socket is mounted there and that ILD's user can open it.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is DockerApiException or System.Text.Json.JsonException))
        {
            Logger?.LogInformation("Could not ask the container engine at {Socket} whether it is rootless ({Reason}); not warning", _options.Socket, ex.Message);
            return null;
        }
    }

    // What the socket said: "Permission denied", not the HTTP client's "Permission denied (docker:80)".
    private static string ReasonOf(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
            if (inner is SocketException socket)
                return socket.Message;
        return ex.Message;
    }

    private async Task<bool?> IsRootlessAsync(CancellationToken ct)
    {
        var (_, podman) = await _api.InfoAsync(libpod: true, ct);
        if (podman?["host"]?["security"]?["rootless"] is JsonValue flag && flag.TryGetValue<bool>(out var rootless))
            return rootless;

        var (_, docker) = await _api.InfoAsync(libpod: false, ct);
        if (docker?["SecurityOptions"] is JsonArray options)
            return options.Any(o => o?.GetValue<string>() == "name=rootless");
        return null;
    }

    public void Dispose()
    {
        _api.Dispose();
        _chatGate.Dispose();
    }
}
