using System.Data.Common;
using System.Net;
using System.Net.NetworkInformation;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// One of ILD's own pieces of infrastructure the egress proxy keeps agents away from by default
/// (<c>docs/adr/0036</c>). <see cref="Addresses"/> are what its <see cref="Hosts"/> resolve to; <see cref="Enabled"/>
/// is the operator's switch, on unless turned off.
/// </summary>
public sealed record ProtectedDestination(string Id, string Name, IReadOnlyList<string> Hosts, IReadOnlyList<IPAddress> Addresses, bool Enabled = true)
{
    /// <summary>Whole ranges the destination covers as well as its <see cref="Addresses"/> (link-local and cloud metadata).</summary>
    public IReadOnlyList<IpRange> Ranges { get; init; } = [];

    public bool Covers(IPAddress address)
        => Addresses.Any(p => IpRange.Single(p).Contains(address)) || Ranges.Any(r => r.Contains(address));

    public const string Ild = "ild";
    public const string Database = "database";
    public const string WorkItemServer = "workitem-server";
    public const string KubernetesApi = "kubernetes-api";
    public const string LinkLocal = "link-local";
    public const string Host = "host";
    public const string Private = "private";

    public static readonly IReadOnlyList<string> AllIds = [Ild, Database, WorkItemServer, KubernetesApi, Host, LinkLocal, Private];

    /// <summary>Link-local addresses, where cloud metadata services answer (169.254.169.254, AWS's IPv6 form), with the credentials of the machine ILD runs on.</summary>
    public static readonly ProtectedDestination LinkLocalRanges = new(LinkLocal, "Link-local and cloud metadata addresses", [], [])
    {
        Ranges = [IpRange.Parse("169.254.0.0/16"), IpRange.Parse("fe80::/10"), IpRange.Parse("fd00:ec2::254/128")],
    };

    /// <summary>Private and cluster-internal addresses (LAN, pod and service networks, storage exports), on isolated run hosts. A declared forward is how a run reaches one.</summary>
    public static readonly ProtectedDestination PrivateRanges = new(Private, "Private and cluster-internal addresses", [], [])
    {
        Ranges = [IpRange.Parse("10.0.0.0/8"), IpRange.Parse("172.16.0.0/12"), IpRange.Parse("192.168.0.0/16"), IpRange.Parse("100.64.0.0/10"), IpRange.Parse("fc00::/7")],
    };

    public string RefusalReason => "protected: " + Name;

    public static string SettingKey(string id) => "network.protect." + id;
}

public interface IProtectedDestinationSource
{
    /// <summary>The destinations ILD derives from its own configuration, each switched on; the policy applies the operator's toggles.</summary>
    ValueTask<IReadOnlyList<ProtectedDestination>> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// The address ranges of ILD's run networks and pods. Always refused (<see cref="EgressDestinationGuard.Check"/>), whatever the
    /// operator's toggles, so one run cannot reach another through the proxy.
    /// </summary>
    ValueTask<IReadOnlyList<IpRange>> GetIsolatedRangesAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<IpRange>>([]);
}

public static class RunNetworkCallers
{
    /// <summary>Whether <paramref name="caller"/> is inside a run network or pod, as a run's agent calling ILD's web port is.</summary>
    public static async ValueTask<bool> IsInRunNetworkAsync(this IProtectedDestinationSource source, IPAddress? caller, CancellationToken ct = default)
    {
        if (caller is null) return false;
        var address = caller.IsIPv4MappedToIPv6 ? caller.MapToIPv4() : caller;
        return (await source.GetIsolatedRangesAsync(ct).ConfigureAwait(false)).Any(r => r.Contains(address));
    }
}

public sealed class ProtectedDestinationSource : IProtectedDestinationSource
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<string, string?> _environment;
    private readonly RunHostKind _kind;
    private readonly TimeProvider _clock;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IReadOnlyList<IPAddress>> _ownAddresses;
    private readonly Func<IReadOnlyList<IPAddress>> _hostAddresses;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private readonly Func<CancellationToken, Task<IReadOnlyList<IpRange>>> _isolated;
    private readonly Func<CancellationToken, Task<IReadOnlyList<IPAddress>>> _controlPlane;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, IPAddress[]> _lastResolved = new(StringComparer.Ordinal);
    private IReadOnlyList<ProtectedDestination>? _lastBuilt;
    private bool _controlPlaneWarned;
    private IReadOnlyList<IpRange> _lastIsolated = [];
    private (IReadOnlyList<ProtectedDestination> Value, IReadOnlyList<IpRange> Isolated, long At)? _cached;

    public ProtectedDestinationSource(IServiceScopeFactory scopes, Func<string, string?> environment, RunHostKind kind, TimeProvider clock,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<IReadOnlyList<IPAddress>>? ownAddresses = null,
        Func<CancellationToken, Task<IReadOnlyList<IpRange>>>? isolatedRanges = null, ILogger? logger = null,
        Func<CancellationToken, Task<IReadOnlyList<IPAddress>>>? controlPlane = null, Func<IReadOnlyList<IPAddress>>? hostAddresses = null)
    {
        _hostAddresses = hostAddresses ?? (() => []);
        _logger = logger;
        _controlPlane = controlPlane ?? (_ => Task.FromResult<IReadOnlyList<IPAddress>>([]));
        _isolated = isolatedRanges ?? (_ => Task.FromResult<IReadOnlyList<IpRange>>([]));
        _scopes = scopes;
        _environment = environment;
        _kind = kind;
        _clock = clock;
        _resolve = resolve ?? EgressDestinationGuard.ResolveAsync;
        _ownAddresses = ownAddresses ?? InterfaceAddresses;
    }

    public async ValueTask<IReadOnlyList<ProtectedDestination>> GetAsync(CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).Value;

    public async ValueTask<IReadOnlyList<IpRange>> GetIsolatedRangesAsync(CancellationToken ct = default)
        => (await LoadAsync(ct).ConfigureAwait(false)).Isolated;

    private async ValueTask<(IReadOnlyList<ProtectedDestination> Value, IReadOnlyList<IpRange> Isolated, long At)> LoadAsync(CancellationToken ct)
    {
        if (Fresh() is { } cached) return cached;
        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } raced) return raced;
            var loaded = (await BuildOrKeepAsync(ct).ConfigureAwait(false), await IsolatedAsync(ct).ConfigureAwait(false), _clock.GetTimestamp());
            _cached = loaded;
            return loaded;
        }
        finally
        {
            _reload.Release();
        }
    }

    // Asking the cluster for its pods can fail (an API blip, throttling). That must not stop all egress, so the ranges that last loaded
    // stay in force, and the failure is cached with them for the TTL rather than retried by every connection.
    private async Task<IReadOnlyList<IpRange>> IsolatedAsync(CancellationToken ct)
    {
        try
        {
            return _lastIsolated = await _isolated(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "Could not list the run networks the egress proxy keeps agents off; keeping the {Count} ranges that last loaded for {Seconds} s",
                _lastIsolated.Count, CacheTtl.TotalSeconds);
            return _lastIsolated;
        }
    }

    // A failure while building the list (the settings store, say) must not leave the proxy with no protected destinations: the list that
    // last built stays in force, as the ranges above do.
    private async Task<IReadOnlyList<ProtectedDestination>> BuildOrKeepAsync(CancellationToken ct)
    {
        try
        {
            return _lastBuilt = await BuildAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && _lastBuilt is not null)
        {
            _logger?.LogWarning(ex, "Could not rebuild the destinations the egress proxy protects; keeping the {Count} that last loaded for {Seconds} s",
                _lastBuilt.Count, CacheTtl.TotalSeconds);
            return _lastBuilt;
        }
    }

    private (IReadOnlyList<ProtectedDestination> Value, IReadOnlyList<IpRange> Isolated, long At)? Fresh()
        => _cached is { } c && _clock.GetElapsedTime(c.At) < CacheTtl ? c : null;

    private async Task<IReadOnlyList<ProtectedDestination>> BuildAsync(CancellationToken ct)
    {
        var result = new List<ProtectedDestination>();

        var ildHosts = HostOf(_environment("ILD_API_URL")).ToList();
        var own = (await ResolveAllAsync(ildHosts, ct).ConfigureAwait(false)).Concat(_ownAddresses()).Distinct().ToList();
        result.Add(new(ProtectedDestination.Ild, "ILD itself", ildHosts, own));

        var databaseHosts = DatabaseHosts(_environment("ILD_DB_CONNECTION_STRING")).ToList();
        if (databaseHosts.Count > 0)
            result.Add(new(ProtectedDestination.Database, "ILD database", databaseHosts, await ResolveAllAsync(databaseHosts, ct).ConfigureAwait(false)));

        string? workItemUrl;
        using (var scope = _scopes.CreateScope())
            workItemUrl = (await scope.ServiceProvider.GetRequiredService<IAppSettingStore>().GetByKeyAsync(AppSettingKeys.WorkItemServerUrl, ct).ConfigureAwait(false))?.Value;
        var workItemHosts = HostOf(workItemUrl).ToList();
        if (workItemHosts.Count > 0)
            result.Add(new(ProtectedDestination.WorkItemServer, "WorkItem Server", workItemHosts, await ResolveAllAsync(workItemHosts, ct).ConfigureAwait(false)));

        if (_kind == RunHostKind.Kubernetes)
        {
            var apiHosts = new List<string>();
            if (_environment("KUBERNETES_SERVICE_HOST") is { Length: > 0 } serviceHost) apiHosts.Add(serviceHost.Trim());
            apiHosts.Add("kubernetes.default.svc");
            var apiAddresses = await ResolveAllAsync(apiHosts, ct).ConfigureAwait(false);
            apiAddresses.AddRange(await ControlPlaneAsync(ct).ConfigureAwait(false));
            result.Add(new(ProtectedDestination.KubernetesApi, "Kubernetes API", apiHosts, apiAddresses.Distinct().ToList()));
        }
        // Ports the compose stack publishes (ILD's own, the WorkItem Server's) answer on the machine's addresses, which are none of the containers'.
        if (_kind == RunHostKind.Container && _hostAddresses().Except(own).ToList() is { Count: > 0 } host)
            result.Add(new(ProtectedDestination.Host, "The machine ILD's container runs on", HostNames, host));
        result.Add(ProtectedDestination.LinkLocalRanges);
        if (_kind is RunHostKind.Container or RunHostKind.Kubernetes) result.Add(ProtectedDestination.PrivateRanges);
        return result;
    }

    private static readonly string[] HostNames = ["host.docker.internal", "gateway.docker.internal", "host.containers.internal"];

    /// <summary>
    /// How the machine a container runs on is reached from inside it: the gateway of each of its networks, which a bridge puts
    /// at the first address of the subnet, and the names the engines give the host. Its other addresses (the LAN's) cannot be known from here.
    /// </summary>
    public static IReadOnlyList<IPAddress> HostMachineAddresses()
    {
        var found = new List<IPAddress>();
        foreach (var properties in NetworkInterface.GetAllNetworkInterfaces().Select(i => i.GetIPProperties()))
        {
            found.AddRange(properties.GatewayAddresses.Select(g => g.Address));
            found.AddRange(properties.UnicastAddresses.Select(a => FirstOfSubnet(a.Address, a.PrefixLength)).OfType<IPAddress>());
        }
        foreach (var name in HostNames)
        {
            try { found.AddRange(Dns.GetHostAddresses(name)); }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException) { }
        }
        return found.Where(a => !EgressDestinationGuard.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Distinct().ToList();
    }

    internal static IPAddress? FirstOfSubnet(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || prefixLength is <= 0 or >= 31) return null;
        var bytes = address.GetAddressBytes();
        var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        var first = (value & (uint.MaxValue << (32 - prefixLength))) + 1;
        return new IPAddress([(byte)(first >> 24), (byte)(first >> 16), (byte)(first >> 8), (byte)first]);
    }

    // The service's endpoints are the API server's real addresses. When they cannot be read the last ones stay, and the ClusterIP still stands.
    private async Task<IReadOnlyList<IPAddress>> ControlPlaneAsync(CancellationToken ct)
    {
        const string key = "kubernetes-endpoints";
        try
        {
            var addresses = (await _controlPlane(ct).ConfigureAwait(false)).ToArray();
            lock (_lastResolved) _lastResolved[key] = addresses;
            return addresses;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!_controlPlaneWarned)
            {
                _controlPlaneWarned = true;
                _logger?.LogWarning(ex, "Could not read the endpoints of the kubernetes service, so the egress proxy refuses the API server only at its ClusterIP and what it last read; grant the Role in docs/kubernetes to cover its real addresses");
            }
            lock (_lastResolved) return _lastResolved.GetValueOrDefault(key) ?? [];
        }
    }

    private async Task<List<IPAddress>> ResolveAllAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        var all = new List<IPAddress>();
        foreach (var host in hosts)
        {
            try
            {
                var resolved = await _resolve(host, ct).ConfigureAwait(false);
                lock (_lastResolved) _lastResolved[host] = resolved;
                all.AddRange(resolved);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A resolver that fails must not turn a protected host into an empty set: what it last resolved to stays.
                lock (_lastResolved)
                    if (_lastResolved.TryGetValue(host, out var last)) all.AddRange(last);
            }
        }
        return all;
    }

    private static IEnumerable<string> HostOf(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? [EgressRules.NormalizeHost(uri.Host)] : [];

    private static IEnumerable<string> DatabaseHosts(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return [];
        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            return [];
        }
        var value = builder.TryGetValue("Host", out var host) ? host : builder.TryGetValue("Server", out var server) ? server : null;
        return (value?.ToString() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(EgressRules.NormalizeHost)
            .Where(h => h.Length > 0);
    }

    private static IReadOnlyList<IPAddress> InterfaceAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(i => i.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => !EgressDestinationGuard.IsLoopback(a))
            .ToList();
}
