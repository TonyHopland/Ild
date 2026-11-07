using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Implementations.Adapters;
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
public sealed class ContainerRunHost : ExecRunHost, IDisposable
{
    public const string RunLeftoverKind = "container-run";

    internal const uint RunnerUid = 10001;

    private readonly ContainerRunHostOptions _options;
    private readonly DockerApiClient _api;
    private readonly ConcurrentDictionary<Guid, string> _addresses = new();
    private string? _warning;
    private bool? _podman;

    // What the store gave each set of toolchains and agents asked for, so a run like one before starts without an installer.
    private readonly ConcurrentDictionary<string, RunToolEnvironment> _tools = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _toolsGate = new(1, 1);
    // Containers that are not a run's, exec'd into by an id of their own: the store's installers.
    private readonly ConcurrentDictionary<Guid, string> _execNames = new();

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

    protected override string IldUrl => _options.IldUrl;

    protected override int EgressProxyPort => _options.EgressProxyPort;

    /// <summary>What a run's container, volume and network are called.</summary>
    internal static string ObjectName(Guid ownerId) => $"ild-run-{ownerId:N}";

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

        try
        {
            var name = ObjectName(owner);
            var tools = await ToolsForAsync(request, ct);
            await TolerateConflictAsync(() => _api.PostAsync("/volumes/create", new { Name = name, Labels = Labels(owner) }, ct));
            await TolerateConflictAsync(() => _api.PostAsync("/networks/create", new { Name = name, Internal = true, CheckDuplicate = true, Labels = Labels(owner) }, ct));
            var ild = await IldAddressAsync(owner, ct);
            await CreateContainerAsync(owner, ild, tools, ct);
            await _api.PostAsync($"/containers/{Escaped(name)}/start", null, ct, HttpStatusCode.NotModified);
            _addresses[owner] = await WaitReadyAsync(owner, ct);

            await PrepareAsync(owner, request, ct);
        }
        catch
        {
            await DeleteQuietlyAsync(owner);
            throw;
        }

        return new RunEnvironment(owner, workingDirectory);
    }

    public override async Task DeleteAsync(RunEnvironment environment, CancellationToken ct = default)
    {
        _addresses.TryRemove(environment.OwnerId, out _);
        await DeleteObjectsAsync(environment.OwnerId, ct);
    }

    // Container first, then the volume it held, then the network once ILD has left it.
    private async Task DeleteObjectsAsync(Guid owner, CancellationToken ct)
    {
        var name = Escaped(ObjectName(owner));
        await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/containers/{name}?force=true&v=true", null, ct));
        await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/volumes/{name}?force=true", null, ct));
        if (_options.IldContainer is { } ild)
        {
            // Refused when ILD was never connected (the run failed before that); a connection that stays is what the network delete reports.
            try { await _api.PostAsync($"/networks/{name}/disconnect", new { Container = ild, Force = true }, ct); }
            catch (DockerApiException) { }
        }
        await IgnoreNotFoundAsync(() => _api.SendAsync(HttpMethod.Delete, $"/networks/{name}", null, ct));
    }

    private async Task DeleteQuietlyAsync(Guid owner)
    {
        try { await DeleteAsync(new RunEnvironment(owner, RunPaths.WorkingDirectory(owner)), CancellationToken.None); }
        catch (Exception ex) { Logger?.LogWarning(ex, "Could not remove the objects of run {Run} after its container failed to start; the next sweep removes them", owner); }
    }

    private static async Task TolerateConflictAsync(Func<Task<JsonNode?>> create)
    {
        try { await create(); }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.Conflict) { }
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
    /// Where the run reaches ILD on its network: ILD's own container, which is connected to the network, when ILD runs
    /// in one, and otherwise the network's gateway, which is the host.
    /// </summary>
    private async Task<string> IldAddressAsync(Guid owner, CancellationToken ct)
    {
        var network = ObjectName(owner);
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
                ?? throw new InvalidOperationException($"ILD's container {ild} has no address on the network of run {owner:N}");
        }

        var created = await _api.GetAsync($"/networks/{Escaped(network)}", ct);
        return created?["IPAM"]?["Config"]?.AsArray().Select(c => c?["Gateway"]?.GetValue<string>()).FirstOrDefault(g => !string.IsNullOrEmpty(g))
            ?? throw new InvalidOperationException($"the network of run {owner:N} has no gateway address, so the run cannot reach ILD");
    }

    private async Task CreateContainerAsync(Guid owner, string ildAddress, RunToolEnvironment? tools, CancellationToken ct)
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
                Binds = tools is { Path.Count: > 0 }
                    ? [$"{name}:{RunPaths.RunsRoot}:rw", $"{_options.ToolsVolume}:{RunToolStore.Root}:ro"]
                    : new[] { $"{name}:{RunPaths.RunsRoot}:rw" },
                Tmpfs = new Dictionary<string, string> { [RunPaths.StateDirectory] = $"rw,nosuid,nodev,size=64m,mode=0700,{await StateOwnerOptionAsync(ct)}" },
                CapDrop = new[] { "ALL" },
                SecurityOpt = new[] { "no-new-privileges" },
                NetworkMode = name,
                ExtraHosts = new[] { $"{_options.IldHost}:{ildAddress}" },
                RestartPolicy = new { Name = "no" },
                Memory = memory,
                NanoCpus = _options.NanoCpus ?? 0,
            },
            NetworkingConfig = new { EndpointsConfig = new Dictionary<string, object> { [name] = new { } } },
        };
        try
        {
            await _api.PostAsync($"/containers/create?name={Escaped(name)}", body, ct);
        }
        catch (DockerApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            // Left by an attempt that failed after making it: started below.
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
        ["ild/role"] = "tools",
    };

    /// <summary>
    /// What the run's commands get from the toolchain store: the toolchains its repository needs and the agents ILD has
    /// installed, installed into the store first when it lacks them. Null when toolchains are off, so the run has only
    /// what the runner image has.
    /// </summary>
    private async Task<RunToolEnvironment?> ToolsForAsync(RunEnvironmentRequest request, CancellationToken ct)
    {
        if (!_options.Toolchains) return null;

        var plan = RunToolDetector.Resolve(request.BasePath);
        var agents = Agents();
        var key = string.Join('\n', RunToolStore.Arguments(plan.Tools, agents));
        // Nothing to give: the run does not mount the store, but still gets its home's .local/bin and npm prefix.
        if (key.Length == 0) return RunToolEnvironment.None;
        if (_tools.TryGetValue(key, out var known)) return known;

        await _toolsGate.WaitAsync(ct);
        try
        {
            if (_tools.TryGetValue(key, out known)) return known;
            await TolerateConflictAsync(() => _api.PostAsync("/volumes/create", new { Name = _options.ToolsVolume, Labels = ToolsLabels() }, ct));

            Logger?.LogInformation("Run {Run} gets {Plan}, and the agents {Agents}, from the toolchain store {Volume}",
                request.OwnerId, plan, string.Join(", ", agents.Select(a => $"{a.Agent.Key}@{a.Version}")), _options.ToolsVolume);
            var environment = await WithInstallerAsync(installer => InstallToolsAsync(installer, plan.Tools, agents, ct), ct);
            Logger?.LogInformation("Run {Run} has {Installed}", request.OwnerId, string.Join(", ", environment.Installed));
            return _tools[key] = environment;
        }
        finally
        {
            _toolsGate.Release();
        }
    }

    /// <summary>
    /// A short-lived container of the runner image with the store mounted writable and the engine's default network, so
    /// mise and npm can download; removed when <paramref name="work"/> ends. It carries no run label, so the sweep never
    /// takes it for a run's.
    /// </summary>
    private async Task<T> WithInstallerAsync<T>(Func<Guid, Task<T>> work, CancellationToken ct)
    {
        var installer = Guid.NewGuid();
        var name = $"ild-tools-{installer:N}";
        _execNames[installer] = name;
        try
        {
            var labels = ToolsLabels();
            labels[RunPaths.InstanceLabel] = _options.Instance;
            try
            {
                await _api.PostAsync($"/containers/create?name={Escaped(name)}", new
                {
                    Image = _options.RunnerImage,
                    Entrypoint = new[] { "/usr/bin/tini", "--" },
                    Cmd = new[] { "sleep", "infinity" },
                    User = $"{RunnerUid}:{RunnerUid}",
                    Env = RunToolStore.InstallerEnvironment,
                    Labels = labels,
                    HostConfig = new
                    {
                        Binds = new[] { $"{_options.ToolsVolume}:{RunToolStore.Root}:rw" },
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
        _podman ??= await _api.InfoAsync(libpod: true, ct) is not null;
        return _podman.Value ? "U" : $"uid={RunnerUid},gid={RunnerUid}";
    }

    private async Task<string> WaitReadyAsync(Guid owner, CancellationToken ct)
    {
        var name = ObjectName(owner);
        var started = Stopwatch.StartNew();
        while (true)
        {
            var container = await _api.GetAsync($"/containers/{Escaped(name)}/json", ct)
                ?? throw new InvalidOperationException($"the container of run {owner:N} is gone");
            if (container["State"]?["Running"]?.GetValue<bool>() == true && AddressIn(container, name) is { } address)
                return address;
            if (container["State"]?["Status"]?.GetValue<string>() is "exited" or "dead")
                throw new InvalidOperationException($"the container of run {owner:N} ended before it was ready: {WhyNotReady(container)}");
            if (started.Elapsed > _options.ReadyTimeout)
                throw new InvalidOperationException($"the container of run {owner:N} was not ready after {_options.ReadyTimeout.TotalSeconds:0} s: {WhyNotReady(container)}");
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

    protected override async Task<bool> RunExistsAsync(Guid owner, CancellationToken ct)
        => await _api.GetAsync($"/containers/{Escaped(ObjectName(owner))}/json", ct, HttpStatusCode.NotFound) is not null;

    protected override async Task<IExecStream> OpenExecStreamAsync(Guid owner, IReadOnlyList<string> command, bool stdin, bool tty, CancellationToken ct)
        => await DockerExec.OpenAsync(_api, _execNames.TryGetValue(owner, out var other) ? other : ObjectName(owner), command, stdin, tty, ExitStatusTimeout, ct);

    // -- Preview -----------------------------------------------------------------

    public override async Task<Uri> PreviewTargetAsync(string workingDirectory, int port, CancellationToken ct = default)
    {
        var owner = OwnerFor(workingDirectory);
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

            return runs
                .Where(run => !Guid.TryParseExact(run, "N", out var id) || !liveIds.Contains(id) && !retained.Contains(id))
                .Select(run => new RunLeftover(RunLeftoverKind, run))
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
    }

    /// <summary>
    /// Puts ILD's own container back on the network of every running run of this instance, at the address each run
    /// was given for ILD. A container that is recreated (an upgrade, <c>docker compose up</c>) starts on none of them,
    /// and without this the runs it left could reach neither the model gateway nor the MCP endpoint, and their previews
    /// could not be reached. A restarted container keeps its networks, and nothing is done.
    /// </summary>
    internal async Task ReconnectToRunsAsync(CancellationToken ct)
    {
        if (_options.IldContainer is not { } ild) return;
        try
        {
            var self = await _api.GetAsync($"/containers/{Escaped(ild)}/json", ct, HttpStatusCode.NotFound);
            if (self is null) return;
            var filter = DockerApiClient.LabelFilter($"{RunPaths.ManagedByLabel}={RunPaths.ManagedByValue}", $"{RunPaths.InstanceLabel}={_options.Instance}");
            foreach (var listed in (await _api.GetAsync($"/containers/json?filters={filter}", ct))?.AsArray() ?? [])
            {
                if (listed?["Labels"]?[RunPaths.RunLabel]?.GetValue<string>() is not { } run || !Guid.TryParseExact(run, "N", out var owner)) continue;
                var network = ObjectName(owner);
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
                    Logger?.LogInformation("Reconnected ILD to the network of run {Run} at {Address}", owner, address ?? "a new address");
                }
                catch (DockerApiException ex)
                {
                    Logger?.LogWarning("Could not put ILD back on the network of run {Run} at {Address}; the run cannot reach ILD until it is retried: {Reason}",
                        owner, address, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or SocketException)
        {
            Logger?.LogWarning(ex, "Could not put ILD back on the networks of its runs at {Socket}", _options.Socket);
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
        var podman = await _api.InfoAsync(libpod: true, ct);
        if (podman?["host"]?["security"]?["rootless"] is JsonValue flag && flag.TryGetValue<bool>(out var rootless))
            return rootless;

        var docker = await _api.InfoAsync(libpod: false, ct);
        if (docker?["SecurityOptions"] is JsonArray options)
            return options.Any(o => o?.GetValue<string>() == "name=rootless");
        return null;
    }

    public void Dispose()
    {
        _api.Dispose();
        _toolsGate.Dispose();
    }
}
