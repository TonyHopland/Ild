using System.Net;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// Where the egress proxy listens. Comes from <c>ILD_NETWORK_PROXY_PORT</c>, the
/// same variable the container entrypoint reads to allow that one port through
/// the agent's firewall rules and that <c>AgentIsolation</c> reads to point the
/// agent's <c>HTTP_PROXY</c> at it — one name, three readers, no drift. Unset
/// means no proxy: nothing listens and no launch is pointed anywhere.
/// </summary>
public sealed record EgressProxyOptions(bool Enabled, int Port)
{
    public static readonly IPAddress ListenAddress = IPAddress.Loopback;

    public static readonly EgressProxyOptions Disabled = new(false, 0);

    public static EgressProxyOptions FromEnvironment()
        => Parse(Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar));

    public static EgressProxyOptions Parse(string? configuredPort)
        => int.TryParse(configuredPort?.Trim(), out var port) && port > 0 && port <= 65535
            ? new EgressProxyOptions(true, port)
            : Disabled;
}
