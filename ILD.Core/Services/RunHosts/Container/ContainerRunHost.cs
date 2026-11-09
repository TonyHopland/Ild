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
            made.Network = await TolerateConflictAsync(() => _api.PostAsync("/networks/create", new { Name = name, Internal = true, CheckDuplicate = true, Labels = Labels(owner) }, ct));
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
        if (_options.IldContainer is { } ild)
        {
            // Refused when ILD was never connected (the run failed before that); a connection that stays is what the network delete reports.
            try { await _api.PostAsync($"/networks/{name}/disconnect", new { Container = ild, Force = true }, ct); }
            catch (DockerApiException) { }
        }
        await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/networks/{name}", null, ct));
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

    /// <summary>Makes the run's container; false when an attempt had left it.</summary>
    private async Task<bool> CreateContainerAsync(Guid owner, string ildAddress, RunToolEnvironment? tools, bool dns, CancellationToken ct)
    {
        var name = ObjectName(owner);
        var memory = _options.MemoryBytes ?? 0;
        var body = new
        {
            Image = _options.RunnerImage,
            Entrypoint = new[] { "/usr/bin/tini", "--" },
            Cmd = new[] { "sleep", "infinity" },
            User = $"{RunnerUid}:{RunnerUid}",
            // The volume's root, which the image gives the runner. An engine makes a missing working directory as root
            // (Podman does), and the runner could then not make the run's own directory in it: the runner makes that.
            WorkingDir = RunPaths.RunsRoot,
            Env = new[] { $"HOME={RunPaths.HomeDirectory(owner)}" }
                .Concat(_options.GitIdentity.Select(e => $"{e.Key}={e.Value}"))
                .Concat(tools?.For(RunPaths.HomeDirectory(owner)) ?? [])
                .ToArray(),
            Labels = Labels(owner),
            HostConfig = new
            {
                Binds = new[] { $"{name}:{RunPaths.RunsRoot}:rw" }.Concat(ToolBinds(tools?.Zones ?? [])).ToArray(),
                Tmpfs = new Dictionary<string, string> { [RunPaths.StateDirectory] = $"rw,nosuid,nodev,size=64m,mode=0700,{await StateOwnerOptionAsync(ct)}" },
                CapDrop = new[] { "ALL" },
                SecurityOpt = new[] { "no-new-privileges" },
                NetworkMode = name,
                ExtraHosts = IldHosts(ildAddress),
                Dns = NameServers(dns),
                RestartPolicy = new { Name = "no" },
                Memory = memory,
                NanoCpus = _options.NanoCpus ?? 0,
            },
            NetworkingConfig = new { EndpointsConfig = new Dictionary<string, object> { [name] = new { } } },
        };
        try
        {
            await _api.PostAsync($"/containers/create?name={Escaped(name)}", body, ct);
            return true;
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            // Left by an attempt that failed after making it, or the run's own on a retry: started by the caller.
            return false;
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"the runner image {_options.RunnerImage} is not on this machine; build or pull it first ({ex.Message})", ex);
        }
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
    /// toolchains read-only for a repository's) and the engine's default network, so mise and npm can download; removed
    /// when <paramref name="work"/> ends. It carries no run label, so the sweep never takes it for a run's.
    /// </summary>
    protected override async Task<T> WithToolInstallerAsync<T>(ToolInstaller toolInstaller, Func<Guid, Task<T>> work, CancellationToken ct)
    {
        var installer = Guid.NewGuid();
        var name = $"ild-tools-{installer:N}";
        _execNames[installer] = name;
        try
        {
            var labels = ToolsLabels();
            labels[RunPaths.InstanceLabel] = _options.Instance;
            string[] binds =
            [
                $"{toolInstaller.Writable.VolumeName(_options.ToolsVolume)}:{toolInstaller.Writable.MountPath}:rw",
                .. toolInstaller.ReadOnly is { } shared ? ToolBinds([shared]) : [],
            ];
            try
            {
                await _api.PostAsync($"/containers/create?name={Escaped(name)}", new
                {
                    Image = _options.RunnerImage,
                    Entrypoint = new[] { "/usr/bin/tini", "--" },
                    Cmd = new[] { "sleep", "infinity" },
                    User = $"{RunnerUid}:{RunnerUid}",
                    Env = toolInstaller.Environment,
                    Labels = labels,
                    HostConfig = new
                    {
                        Binds = binds,
                        CapDrop = new[] { "ALL" },
                        SecurityOpt = new[] { "no-new-privileges" },
                        RestartPolicy = new { Name = "no" },
                    },
                }, ct);
            }
            catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"the runner image {_options.RunnerImage} is not on this machine; build or pull it first ({ex.Message})", ex);
            }
            await _api.PostAsync($"/containers/{Escaped(name)}/start", null, ct, HttpStatusCode.NotModified);
            await WaitRunningAsync(name, ct);
            return await work(installer);
        }
        finally
        {
            _execNames.TryRemove(installer, out _);
            await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{Escaped(name)}?force=true", null, CancellationToken.None));
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
        => owner.Chat ? await ChatContainerRunningAsync(ct) : await _api.GetAsync($"/containers/{Escaped(owner.Chat ? ChatName : ObjectName(owner.Id))}/json", ct, HttpStatusCode.NotFound) is not null;

    protected override async Task<IExecStream> OpenExecStreamAsync(ExecOwner owner, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
    {
        if (owner.Chat)
            return await DockerExec.OpenAsync(_api, await EnsureChatContainerAsync(ct), command, stdin, tty, ExitStatusTimeout, ct);
        if (_execNames.TryGetValue(owner.Id, out var other))
            return await DockerExec.OpenAsync(_api, other, command, stdin, tty, ExitStatusTimeout, ct);
        try
        {
            return await DockerExec.OpenAsync(_api, ObjectName(owner.Id), command, stdin, tty, ExitStatusTimeout, ct);
        }
        catch (DockerApiException)
        {
            // Stopped when ILD shut down, or by a restart of the engine or machine: started again, with all it held. Engines
            // refuse an exec into a stopped container with different statuses (Docker 409, Podman 500), so its state decides.
            if (!await RunContainerStoppedAsync(owner.Id, ct)) throw;
            await StartStoppedRunAsync(owner.Id, ct);
            return await DockerExec.OpenAsync(_api, ObjectName(owner.Id), command, stdin, tty, ExitStatusTimeout, ct);
        }
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
        if (await ChatContainerRunningAsync(ct)) return ChatName;

        await _chatGate.WaitAsync(ct);
        try
        {
            if (await ChatContainerRunningAsync(ct)) return ChatName;

            await TolerateConflictAsync(() => _api.PostAsync("/volumes/create", new { Name = ChatName, Labels = ChatLabels() }, ct));
            if (_options.Toolchains)
                foreach (var zone in ChatToolZones)
                    await PrepareToolZoneAsync(zone, ct);
            await TolerateConflictAsync(() => _api.PostAsync("/networks/create", new { Name = ChatName, Internal = true, CheckDuplicate = true, Labels = ChatLabels() }, ct));
            // A stopped one is replaced; what it held is in the volume.
            await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{Escaped(ChatName)}?force=true", null, ct));
            var ild = await IldAddressAsync(ChatName, "the chat container", ct);
            await CreateChatContainerAsync(ild, ct);
            await _api.PostAsync($"/containers/{Escaped(ChatName)}/start", null, ct, HttpStatusCode.NotModified);
            await WaitReadyAsync(ChatName, "the chats", ct);
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
        var body = new
        {
            Image = _options.RunnerImage,
            Entrypoint = new[] { "/usr/bin/tini", "--" },
            Cmd = new[] { "sleep", "infinity" },
            User = $"{RunnerUid}:{RunnerUid}",
            WorkingDir = RunPaths.ChatsRoot,
            Env = _options.GitIdentity.Select(e => $"{e.Key}={e.Value}").ToArray(),
            Labels = new Dictionary<string, string>(ChatLabels()) { [SpecLabel] = await ChatSpecAsync(ct) },
            HostConfig = new
            {
                // Every chat shares this container, so it mounts the agents and the shared toolchains, never a repository's own tools.
                Binds = new[] { $"{ChatName}:{RunPaths.ChatsRoot}:rw" }.Concat(ToolBinds(_options.Toolchains ? ChatToolZones : [])).ToArray(),
                Tmpfs = new Dictionary<string, string> { [RunPaths.StateDirectory] = $"rw,nosuid,nodev,size=64m,mode=0700,{await StateOwnerOptionAsync(ct)}" },
                CapDrop = new[] { "ALL" },
                SecurityOpt = new[] { "no-new-privileges" },
                NetworkMode = ChatName,
                ExtraHosts = IldHosts(ildAddress),
                Dns = NameServers(dns: false),
                RestartPolicy = new { Name = "no" },
            },
            NetworkingConfig = new { EndpointsConfig = new Dictionary<string, object> { [ChatName] = new { } } },
        };
        try
        {
            await _api.PostAsync($"/containers/create?name={Escaped(ChatName)}", body, ct);
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            // Made by another request a moment ago: started above.
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"the runner image {_options.RunnerImage} is not on this machine; build or pull it first ({ex.Message})", ex);
        }
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
            try
            {
                await _api.PostAsync($"/containers/create?name={Escaped(name)}", new
                {
                    Image = _options.RunnerImage,
                    Entrypoint = new[] { "/usr/bin/tini", "--" },
                    Cmd = new[] { "sleep", "infinity" },
                    User = $"{RunnerUid}:{RunnerUid}",
                    Env = new[] { $"HOME={SignInHome}" }.Concat(tools?.For(SignInHome) ?? []).ToArray(),
                    Labels = SignInLabels(),
                    // The engine's default network, as the toolchain installer has; no volume of a run or chat, no ILD address.
                    HostConfig = new
                    {
                        Binds = ToolBinds(tools?.Zones ?? []).ToArray(),
                        CapDrop = new[] { "ALL" },
                        SecurityOpt = new[] { "no-new-privileges" },
                        RestartPolicy = new { Name = "no" },
                    },
                }, ct);
            }
            catch (DockerApiException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"the runner image {_options.RunnerImage} is not on this machine; build or pull it first ({ex.Message})", ex);
            }
            await _api.PostAsync($"/containers/{Escaped(name)}/start", null, ct, HttpStatusCode.NotModified);
            await WaitRunningAsync(name, ct);

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

            Collect(await _api.GetAsync($"/containers/json?all=true&filters={filter}", ct), c => c["Labels"]);
            Collect((await _api.GetAsync($"/volumes?filters={filter}", ct))?["Volumes"], v => v["Labels"]);
            Collect(await _api.GetAsync($"/networks?filters={filter}", ct), n => n["Labels"]);
            var signIns = new List<string>();
            var open = _execNames.Values.ToHashSet(StringComparer.Ordinal);
            foreach (var listed in (await _api.GetAsync($"/containers/json?all=true&filters={filter}", ct))?.AsArray() ?? [])
            {
                var role = listed?["Labels"]?[RunPaths.RoleLabel]?.GetValue<string>();
                if (role is SignInRole or RunPaths.ToolsRole
                    && listed!["Names"]?.AsArray().FirstOrDefault()?.GetValue<string>()?.TrimStart('/') is { } name
                    && !_signIns.ContainsKey(name) && !open.Contains(name))
                    signIns.Add(name);
            }

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
                return true;
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException)
            {
                Logger?.LogWarning(ex, "Could not delete the sign-in or installer container {Name}; the next sweep tries again", leftover.Location);
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
                    await _api.PostAsync($"/networks/{Escaped(network)}/connect", address is null
                        ? new { Container = ild }
                        : new { Container = ild, EndpointConfig = new { IPAMConfig = new { IPv4Address = address } } }, ct);
                    Logger?.LogInformation("Reconnected ILD to the network of {Subject} at {Address}", subject, address ?? "a new address");
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
