using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts.Isolated;
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

    public ContainerRunHost(ContainerRunHostOptions options, IProcessRunner runner, ILogger? logger = null)
        : base(runner, logger)
    {
        _options = options;
        _api = new DockerApiClient(options.Socket);
    }

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
            await TolerateConflictAsync(() => _api.PostAsync("/volumes/create", new { Name = name, Labels = Labels(owner) }, ct));
            await TolerateConflictAsync(() => _api.PostAsync("/networks/create", new { Name = name, Internal = true, CheckDuplicate = true, Labels = Labels(owner) }, ct));
            var ild = await IldAddressAsync(owner, ct);
            await CreateContainerAsync(owner, ild, ct);
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

    private async Task CreateContainerAsync(Guid owner, string ildAddress, CancellationToken ct)
    {
        var name = ObjectName(owner);
        var memory = _options.MemoryBytes ?? 0;
        var body = new
        {
            Image = _options.RunnerImage,
            Entrypoint = new[] { "/usr/bin/tini", "--" },
            Cmd = new[] { "sleep", "infinity" },
            User = $"{RunnerUid}:{RunnerUid}",
            WorkingDir = RunPaths.RunRoot(owner),
            Env = new[] { $"HOME={RunPaths.HomeDirectory(owner)}" }.Concat(_options.GitIdentity.Select(e => $"{e.Key}={e.Value}")).ToArray(),
            Labels = Labels(owner),
            HostConfig = new
            {
                Binds = new[] { $"{name}:{RunPaths.RunsRoot}:rw" },
                Tmpfs = new Dictionary<string, string> { [RunPaths.StateDirectory] = $"rw,nosuid,nodev,size=64m,mode=0700,uid={RunnerUid},gid={RunnerUid}" },
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
        => await DockerExec.OpenAsync(_api, ObjectName(owner), command, stdin, tty, ExitStatusTimeout, ct);

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
    }

    /// <summary>
    /// Says so when the socket is rootful: whoever holds it controls every container on the host, which is root there.
    /// Asks Podman's own info first, then Docker's; says nothing when neither answers or neither tells.
    /// </summary>
    public override async Task<string?> WarningAsync(CancellationToken ct = default)
    {
        if (_warning is not null) return _warning;
        try
        {
            // A socket that stalls must not hold the settings page for the HTTP client's own five minutes.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var rootless = await IsRootlessAsync(limit.Token);
            if (rootless != false) return null;
            return _warning = $"The container engine at {_options.Socket} is not rootless, so whoever holds that socket is root on this machine. Use rootless Podman (or rootless Docker) for runs.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is DockerApiException or HttpRequestException or IOException or SocketException or System.Text.Json.JsonException or OperationCanceledException))
        {
            Logger?.LogInformation("Could not ask the container engine at {Socket} whether it is rootless ({Reason}); not warning", _options.Socket, ex.Message);
            return null;
        }
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

    public void Dispose() => _api.Dispose();
}
