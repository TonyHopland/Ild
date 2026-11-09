using System.Data.Common;
using System.Net;
using System.Net.NetworkInformation;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// One of ILD's own pieces of infrastructure the egress proxy keeps agents away from by default
/// (<c>docs/adr/0036</c>). <see cref="Addresses"/> are what its <see cref="Hosts"/> resolve to; <see cref="Enabled"/>
/// is the operator's switch, on unless turned off.
/// </summary>
public sealed record ProtectedDestination(string Id, string Name, IReadOnlyList<string> Hosts, IReadOnlyList<IPAddress> Addresses, bool Enabled = true)
{
    public const string Ild = "ild";
    public const string Database = "database";
    public const string WorkItemServer = "workitem-server";
    public const string KubernetesApi = "kubernetes-api";

    public static readonly IReadOnlyList<string> AllIds = [Ild, Database, WorkItemServer, KubernetesApi];

    public string RefusalReason => "protected: " + Name;

    public static string SettingKey(string id) => "network.protect." + id;
}

public interface IProtectedDestinationSource
{
    /// <summary>The destinations ILD derives from its own configuration, each switched on; the policy applies the operator's toggles.</summary>
    ValueTask<IReadOnlyList<ProtectedDestination>> GetAsync(CancellationToken ct = default);
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
    private readonly SemaphoreSlim _reload = new(1, 1);
    private (IReadOnlyList<ProtectedDestination> Value, long At)? _cached;

    public ProtectedDestinationSource(IServiceScopeFactory scopes, Func<string, string?> environment, RunHostKind kind, TimeProvider clock,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, Func<IReadOnlyList<IPAddress>>? ownAddresses = null)
    {
        _scopes = scopes;
        _environment = environment;
        _kind = kind;
        _clock = clock;
        _resolve = resolve ?? EgressDestinationGuard.ResolveAsync;
        _ownAddresses = ownAddresses ?? InterfaceAddresses;
    }

    public async ValueTask<IReadOnlyList<ProtectedDestination>> GetAsync(CancellationToken ct = default)
    {
        if (Fresh() is { } cached) return cached;
        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } raced) return raced;
            var value = await BuildAsync(ct).ConfigureAwait(false);
            _cached = (value, _clock.GetTimestamp());
            return value;
        }
        finally
        {
            _reload.Release();
        }
    }

    private IReadOnlyList<ProtectedDestination>? Fresh()
        => _cached is { } c && _clock.GetElapsedTime(c.At) < CacheTtl ? c.Value : null;

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
            result.Add(new(ProtectedDestination.KubernetesApi, "Kubernetes API", apiHosts, await ResolveAllAsync(apiHosts, ct).ConfigureAwait(false)));
        }
        return result;
    }

    private async Task<List<IPAddress>> ResolveAllAsync(IEnumerable<string> hosts, CancellationToken ct)
    {
        var all = new List<IPAddress>();
        foreach (var host in hosts)
        {
            try
            {
                all.AddRange(await _resolve(host, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
            {
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
