using ILD.Core.Services.RunHosts.Local;
using System.Net;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Container;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Where the egress proxy listens, and how a launch is told to reach it. Comes
/// from <c>ILD_NETWORK_PROXY_PORT</c>, the same variable the container entrypoint
/// reads to allow that one port through the agent's firewall rules — one name,
/// and this is the only parser of it on the app side, so the port the proxy binds
/// and the port a launch's <c>HTTP_PROXY</c> names cannot drift. Unset means no
/// proxy: nothing listens and no launch is pointed anywhere.
/// </summary>
public sealed record EgressProxyOptions(bool Enabled, int Port)
{
    public static readonly IPAddress ListenAddress = IPAddress.Loopback;

    /// <summary>What the proxy binds. Loopback, except where the agent runs in a container or pod and reaches the proxy at ILD's address
    /// (the address differs on every run's network, so no narrower one serves them all; <see cref="AcceptsClient"/> and the credential do the narrowing).</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>
    /// Whom the proxy serves, besides a valid credential: a connection from any other address is dropped before a byte is read.
    /// Null serves every address. Narrows a bind that cannot be narrow.
    /// </summary>
    public Func<IPAddress, bool>? AcceptsClient { get; init; }

    /// <summary>Where the model gateway's TLS listener binds: loopback for local runs, else every interface, where the runs reach ILD.</summary>
    public static IPAddress BindAddressFor(RunHostKind kind) => kind == RunHostKind.Local ? IPAddress.Loopback : IPAddress.Any;

    /// <summary>
    /// Whom the proxy serves for a run host. Local: loopback is all that can connect. Container: ILD has a different address on each
    /// run's network, so no single bind fits and the proxy binds every interface; it answers loopback and the subnet pool the run
    /// networks are cut from, and drops the rest (compose peers, the LAN). Kubernetes: every pod's route to ILD is its own, so there
    /// is no filter, and the credential and the optional ingress NetworkPolicy do the narrowing.
    /// </summary>
    public static Func<IPAddress, bool>? ClientFilterFor(RunHostKind kind, Func<string, string?> setting)
    {
        if (kind != RunHostKind.Container) return null;
        var pool = IpRange.TryParse(setting(ContainerRunHostOptions.SubnetPoolSetting)?.Trim() ?? string.Empty, out var configured)
            ? configured
            : IpRange.Parse(ContainerRunHostOptions.DefaultSubnetPool);
        return address => EgressDestinationGuard.IsLoopback(address) || pool.Contains(address);
    }

    public static readonly EgressProxyOptions Disabled = new(false, 0);

    /// <summary>The longest zone name the log column holds.</summary>
    public const int MaxZoneLength = 64;

    /// <summary>
    /// ILD's own plain-HTTP port on loopback, where the proxy hands a request for an agents' preview hostname
    /// (<see cref="Interfaces.PreviewProxyBase.AgentHost"/>); null when ILD listens on no plain-HTTP port it can name.
    /// </summary>
    public int? WebPort { get; init; }

    public static EgressProxyOptions FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar)) with
        {
            WebPort = ParseWebPort(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"), Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")),
        };

    /// <summary>
    /// The port of the first <c>http://</c> address in <c>ASPNETCORE_URLS</c> (<c>http://+:8080;https://…</c>), else the
    /// first of <c>ASPNETCORE_HTTP_PORTS</c>, which ASP.NET Core reads only when the first is unset.
    /// </summary>
    internal static int? ParseWebPort(string? urls, string? httpPorts)
    {
        foreach (var url in (urls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
            var authority = url["http://".Length..].Split('/', 2)[0];
            var colon = authority.LastIndexOf(':');
            return colon >= 0 && colon > authority.LastIndexOf(']') && int.TryParse(authority[(colon + 1)..], out var port) && port is > 0 and <= 65535
                ? port
                : 80;
        }
        if (!string.IsNullOrWhiteSpace(urls)) return null;
        foreach (var value in (httpPorts ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(value, out var port) && port is > 0 and <= 65535)
                return port;
        return null;
    }

    public static EgressProxyOptions Parse(string? configuredPort)
        => int.TryParse(configuredPort?.Trim(), out var port) && port > 0 && port <= 65535
            ? new EgressProxyOptions(true, port)
            : Disabled;

    /// <summary>
    /// The URL a launch's <c>HTTP_PROXY</c> is set to, or <c>null</c> when the proxy is disabled. It always carries a credential
    /// (<see cref="EgressCredentials"/>), which the proxy requires: with an <paramref name="aiProviderId"/> it also scopes the launch to
    /// that provider's list entries. The scope is in the signed token, so the launch cannot change it.
    /// </summary>
    public string? ClientUrl(Guid? aiProviderId)
        => Enabled ? UrlFor(new LaunchScope(Provider: aiProviderId)) : null;

    /// <summary>The URL a toolchain installer for <paramref name="zone"/> (<c>agents</c>, <c>toolchains</c>, <c>repo-&lt;id&gt;</c>) is pointed at, or null when the proxy is disabled.</summary>
    public string? InstallerUrl(string zone)
        => Enabled ? UrlFor(new LaunchScope(Installer: zone)) : null;

    private string UrlFor(LaunchScope scope)
        => $"http://{EgressCredentials.User}:{EgressCredentials.Current.Mint(scope)}@{ListenAddress}:{Port}";
}
