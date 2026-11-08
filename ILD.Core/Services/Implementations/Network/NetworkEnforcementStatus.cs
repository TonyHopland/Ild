using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations.Network;

/// <summary>
/// The egress proxy a deployment runs, and how much its network rules hold for the runs of a run host:
/// <c>enforced</c> where runs and chats sit in containers or pods whose network reaches only ILD, so the
/// proxy is their only way out, or <c>advisory</c> where they run on ILD's own machine and only clients
/// that honour <c>HTTP_PROXY</c> go through it.
/// </summary>
public sealed record NetworkEnforcementStatus(bool ProxyEnabled, int? ProxyPort)
{
    public const string Enforced = "enforced";
    public const string Advisory = "advisory";

    public static NetworkEnforcementStatus FromEnvironment(EgressProxyOptions proxy)
        => new(proxy.Enabled, proxy.Enabled ? proxy.Port : null);

    /// <summary>Whether the rules are enforced for runs on a host of <paramref name="kind"/>.</summary>
    public static string EnforcementFor(RunHostKind kind) => kind == RunHostKind.Local ? Advisory : Enforced;

    /// <summary>Why the rules are or are not enforced for runs on a host of <paramref name="kind"/>.</summary>
    public string ReasonFor(RunHostKind kind)
        => (kind == RunHostKind.Local, ProxyEnabled) switch
        {
            (false, true) => "Runs and chats sit on a network that reaches only ILD, so the egress proxy is their only way out.",
            (false, false) => $"Runs and chats sit on a network that reaches only ILD, and {AgentIsolation.EgressProxyPortEnvVar} is not set, so they have no way out.",
            (true, true) => "Runs and chats run on ILD's own machine: the proxy judges and logs every connection that honours HTTP_PROXY, but traffic that ignores it is not stopped.",
            (true, false) => $"Runs and chats run on ILD's own machine and {AgentIsolation.EgressProxyPortEnvVar} is not set, so no egress proxy is running and agent launches are not pointed at one.",
        };
}
