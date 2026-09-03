using System.Net;

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

    public static readonly EgressProxyOptions Disabled = new(false, 0);

    /// <summary>
    /// The Basic-auth user name on a provider-scoped proxy URL. Some proxy clients
    /// (undici among them) send <c>Proxy-Authorization</c> only when both user and
    /// password are present, so the provider id travels as the password behind
    /// this fixed name.
    /// </summary>
    public const string ScopeUser = "provider";

    public static EgressProxyOptions FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar));

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
}
