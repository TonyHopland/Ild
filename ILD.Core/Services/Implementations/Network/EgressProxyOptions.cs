using ILD.Core.Services.RunHosts.Local;
using System.Net;
using ILD.Core.Services.RunHosts;

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

    /// <summary>What the proxy binds. Loopback, except where the agent runs in a container or pod and reaches the proxy at ILD's address.</summary>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    public static IPAddress BindAddressFor(RunHostKind kind) => kind == RunHostKind.Local ? IPAddress.Loopback : IPAddress.Any;

    public static readonly EgressProxyOptions Disabled = new(false, 0);

    /// <summary>
    /// The Basic-auth user name on a provider-scoped proxy URL. Some proxy clients
    /// (undici among them) send <c>Proxy-Authorization</c> only when both user and
    /// password are present, so the provider id travels as the password behind
    /// this fixed name.
    /// </summary>
    public const string ScopeUser = "provider";

    /// <summary>The Basic-auth user name on an installer-scoped proxy URL, whose password is the toolchain zone the installer fills.</summary>
    public const string InstallerUser = "installer";

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
    /// The URL a launch's <c>HTTP_PROXY</c> is set to, or <c>null</c> when the
    /// proxy is disabled. With an <paramref name="aiProviderId"/> the URL carries
    /// the provider as credentials, which the proxy reads back out of
    /// <c>Proxy-Authorization</c> to apply that provider's scoped list entries.
    /// </summary>
    public string? ClientUrl(Guid? aiProviderId)
    {
        if (!Enabled) return null;
        return aiProviderId is { } id
            ? $"http://{ScopeUser}:{id:D}@{ListenAddress}:{Port}"
            : $"http://{ListenAddress}:{Port}";
    }

    /// <summary>The URL a toolchain installer for <paramref name="zone"/> (<c>agents</c>, <c>toolchains</c>, <c>repo-&lt;id&gt;</c>) is pointed at, or null when the proxy is disabled.</summary>
    public string? InstallerUrl(string zone)
        => Enabled ? $"http://{InstallerUser}:{zone}@{ListenAddress}:{Port}" : null;
}
